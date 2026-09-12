// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class CheckpointStore
{
    // 先古回血的基础目标，也是新局/进入楼层时的固定血量。
    private const int ActOneHealth = 4;
    internal const int InitialMaxHealth = 86;
    private const string CheckpointVersion = "deathless-run-checkpoint-v4";
    private static readonly string CheckpointPath = Path.Combine(
        RecoveryMarker.ModDirectory,
        "deathless-run.checkpoint.json");
    private static readonly string CheckpointVersionPath = Path.Combine(
        RecoveryMarker.ModDirectory,
        "deathless-run.checkpoint.version");
    // 旧版“删牌后回归补牌”队列不再参与任何逻辑，只在清理时删除遗留文件。
    private static readonly string LegacyPendingRestoredCardsPath = Path.Combine(
        RecoveryMarker.ModDirectory,
        "deathless-run.pending-restored-cards.json");
    private static readonly object Sync = new();

    public static void CaptureCurrentAct(RunManager runManager, AbstractRoom? preFinishedRoom = null)
    {
        try
        {
            var snapshot = runManager.ToSave(preFinishedRoom);
            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(snapshot));
            File.WriteAllText(CheckpointVersionPath, CheckpointVersion);
            RecoveryMarker.Complete();
            ModLog.Write($"Checkpoint captured for act {snapshot.CurrentActIndex}; pre-finished room: {preFinishedRoom is not null}.");
        }
        catch (Exception exception)
        {
            // A failed checkpoint leaves the normal game flow untouched.
            ModLog.Write($"Checkpoint capture failed: {exception}");
        }
    }

    public static void SetAncientHealthForCompletedAct(RunManager runManager)
    {
        var state = runManager.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 0)
            return;

        foreach (var player in state.Players)
            player.Creature.SetCurrentHpInternal(ActOneHealth);

        ModLog.Write($"First Ancient event completed; player health set to {ActOneHealth}.");
    }

    public static void SetInitialHealth(RunManager runManager)
    {
        var state = runManager.DebugOnlyGetState();
        if (state is null)
            return;

        foreach (var player in state.Players)
            player.Creature.SetMaxHpInternal(InitialMaxHealth);

        ModLog.Write($"New run started; initial max health set to {InitialMaxHealth}. Current health remains unchanged until the first Ancient choice.");
    }

    public static async Task ApplyRecoveryState(RunState state, SerializableRun checkpoint)
    {
        var count = Math.Min(state.Players.Count, checkpoint.Players.Count);
        for (var i = 0; i < count; i++)
        {
            var player = state.Players[i];
            var savedPlayer = checkpoint.Players[i];

            // 傲慢线触发后的隐藏效果：回归不再添加任何诅咒，且血量与血量上限
            // 各 +1。加成同时写回检查点，多次回归持续累计。
            var hpBonus = RouteState.IsPrideRoute ? 1 : 0;
            savedPlayer.MaxHp += hpBonus;
            savedPlayer.CurrentHp += hpBonus;

            // Set the runtime value after saved-run setup as well as preserving
            // the serialized value, so max HP gains survive the return.
            player.Creature.SetMaxHpInternal(savedPlayer.MaxHp);
            CardModel? curse;
            if (AmnesiaState.TimelineHidden)
            {
                // 失忆回归：卡组重置为开局初始状态，且不添加回归诅咒。
                curse = null;
                // 失忆线仍按剧情规则静默重建初始卡组。
                await ResetDeckToInitialAsync(player);
                ModLog.Write("Amnesia recovery: deck reset to the initial state; no curse added.");
            }
            else
            {
                var guiltyCount = player.Deck.Cards.Count(card => card is Guilty);
                curse = RouteState.IsPrideRoute
                    ? null
                    : guiltyCount >= 3
                        ? state.CreateCard<Injury>(player)
                        : state.CreateCard<Guilty>(player);

                if (curse is not null)
                {
                    try
                    {
                        var addResult = await CardPileCmd.Add(curse, PileType.Deck);
                        CardCmd.PreviewCardPileAdd(addResult);
                        ModLog.Write($"Recovery curse added with the native deck animation: {curse.Id}.");
                    }
                    catch (Exception exception)
                    {
                        ModLog.Write($"Animated recovery curse add failed; using silent fallback: {exception}");
                        player.Deck.AddInternal(curse, silent: true);
                        try
                        {
                            RecordCardGains(new[] { curse }, "recovery curse fallback");
                        }
                        catch (Exception persistenceException)
                        {
                            ModLog.Write($"Recovery curse checkpoint update failed: {persistenceException.Message}");
                        }
                    }
                }

                if (RouteState.IsPrideRoute)
                    ModLog.Write("Pride route recovery: no curse added.");
            }
            // 回归血量使用检查点保存的 CurrentHp（傲慢线已含 +1 加成）。
            player.Creature.SetCurrentHpInternal(savedPlayer.CurrentHp);
            ModLog.Write($"Recovery health restored from checkpoint: {savedPlayer.CurrentHp}/{savedPlayer.MaxHp}; curse added: {curse?.Id}.");
        }

        if (RouteState.IsPrideRoute)
        {
            // 把 +1 加成写回检查点文件：下一次死亡回归以新的血量基准继续累计。
            try
            {
                File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
                ModLog.Write("Pride route HP bonus written back to the checkpoint.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Could not write the pride HP bonus back to the checkpoint: {exception.Message}");
            }
        }

        ModLog.Write("Recovery state applied; recovery curse branch applied.");
    }

    // 以 StartTime 区分各局，供本 mod 的按局标记文件使用。
    internal static string GetRunKey(SerializableRun checkpoint) =>
        checkpoint.StartTime.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // 失忆回归：清空检查点文件里的已走节点并回写（同样保留最后的当前位置，
    // 避免下一次回归无法前进）。
    internal static void ClearCheckpointVisitedCoords(SerializableRun checkpoint)
    {
        try
        {
            var coords = checkpoint.VisitedMapCoords;
            if (coords.Count > 0)
            {
                var current = coords[^1];
                coords.Clear();
                coords.Add(current);
            }

            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            ModLog.Write("Amnesia recovery: checkpoint visited map coords cleared.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not persist the amnesia checkpoint: {exception.Message}");
        }
    }

    public static void CaptureAncientCompletion(RunManager runManager)
    {
        var state = runManager.DebugOnlyGetState();
        if (state?.CurrentRoom is not EventRoom currentRoom)
        {
            ModLog.Write("Ancient checkpoint skipped because no event room was active.");
            return;
        }

        // Saving this room as pre-finished makes native LoadRun reconstruct the
        // Ancient's completed page, so only its Continue button remains.
        CaptureCurrentAct(runManager, currentRoom);
    }

    public static bool TryPrepareRecovery(SerializableRun deathState)
    {
        if (!TryLoad(out var checkpoint))
            return false;

        // 死亡时牌组不再覆盖检查点。获得卡牌和升级已经在发生当刻增量写入，
        // 删除与附魔等未登记变化自然随时间线回滚。
        AmnesiaState.CaptureDeathEpisode(checkpoint, deathState);
        CopyPersistentNonDeckState(deathState, checkpoint);

        try
        {
            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            try { File.Delete(LegacyPendingRestoredCardsPath); } catch { }
            ModLog.Write("Non-deck persistent death state merged into checkpoint; checkpoint deck kept unchanged.");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Death-state merge failed: {exception}");
            return false;
        }
    }

    // 失忆回归：静默清空当前卡组并按开局快照重建。不走 CardPileCmd 的删牌
    // 命令——不播放删牌动画，也不会触发监听删牌命令的效果（例如傲慢结局
    // 对莱茵哈鲁特被移除的监听）。
    private static Task ResetDeckToInitialAsync(Player player)
    {
        var initialDeck = AmnesiaState.GetInitialDeck();
        if (initialDeck.Count == 0)
        {
            ModLog.Write("Amnesia deck reset skipped: no initial deck snapshot was captured.");
            return Task.CompletedTask;
        }

        foreach (var card in player.Deck.Cards.ToArray())
            player.Deck.RemoveInternal(card, silent: true);

        foreach (var savedCard in initialDeck)
        {
            var card = player.RunState.LoadCard(savedCard, player);
            player.Deck.AddInternal(card, silent: true);
        }

        ModLog.Write($"Amnesia recovery: deck reset to the initial {initialDeck.Count} cards (silent).");
        return Task.CompletedTask;
    }

    public static bool TryLoad(out SerializableRun checkpoint)
    {
        checkpoint = null!;
        try
        {
            if (File.ReadAllText(CheckpointVersionPath) != CheckpointVersion)
                return false;

            var result = JsonSerializationUtility.FromJson<SerializableRun>(File.ReadAllText(CheckpointPath));
            if (!result.Success || result.SaveData is null)
                return false;

            checkpoint = result.SaveData!;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Checkpoint load failed: {exception}");
            return false;
        }
    }

    public static void Clear()
    {
        try { File.Delete(CheckpointPath); } catch { }
        try { File.Delete(CheckpointVersionPath); } catch { }
        try { File.Delete(LegacyPendingRestoredCardsPath); } catch { }
    }

    private static void CopyPersistentNonDeckState(
        SerializableRun from,
        SerializableRun to)
    {
        // 地图涂鸦属于玩家跨时间线留下的信息：死亡时最新的线条覆盖检查点
        // 中较旧的涂鸦，回归载入时由原版 MapDrawingsToLoad 自动恢复。
        to.MapDrawings = from.MapDrawings;

        var count = Math.Min(from.Players.Count, to.Players.Count);
        for (var i = 0; i < count; i++)
        {
            var source = from.Players[i];
            var destination = to.Players[i];

            // 药水、最大生命和药水槽属于检查点状态，不再被死亡时状态覆盖。
            // MaxEnergy/BaseOrbSlotCount 仍按原有规则保留死亡时成长。
            destination.MaxEnergy = source.MaxEnergy;
            destination.BaseOrbSlotCount = source.BaseOrbSlotCount;

            // Keep the checkpoint's CurrentHp. It is captured after the Ancient
            // choice and is the health that recovery must restore.
        }
    }

    // 获得卡牌时只把新增的牌追加到检查点，不复制当前整副牌组；这样此前
    // 发生的删牌不会被顺带永久化。调用方必须只传入已成功进入 Deck 的牌。
    public static bool TryGetPermanentDeckOwner(CardModel? card, out Player owner)
    {
        owner = null!;
        if (card is null)
            return false;

        try
        {
            var candidate = card.Owner;
            var deck = candidate?.Deck;
            var deckCards = deck?.Cards;
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (candidate is null || deck is null || deckCards is null || state is null ||
                !state.Players.Any(player =>
                    ReferenceEquals(player, candidate) || player.NetId == candidate.NetId) ||
                !deckCards.Any(deckCard => ReferenceEquals(deckCard, card)))
            {
                return false;
            }

            owner = candidate;
            return true;
        }
        catch
        {
            // 其他 mod 可能创建仅用于标题、预览或求解的半初始化卡牌。
            // 观察器对这种卡必须静默跳过，不能影响原调用。
            return false;
        }
    }

    public static void RecordCardGains(IEnumerable<CardModel> cards, string reason)
    {
        var gained = cards
            .Distinct((IEqualityComparer<CardModel>)ReferenceEqualityComparer.Instance)
            .ToList();
        if (gained.Count == 0 || !File.Exists(CheckpointPath) || !File.Exists(CheckpointVersionPath))
            return;

        lock (Sync)
        {
            if (!TryLoad(out var checkpoint))
                return;

            var changed = 0;
            foreach (var card in gained)
            {
                if (!TryGetPermanentDeckOwner(card, out var owner))
                    continue;

                var playerIndex = checkpoint.Players.FindIndex(player => player.NetId == owner.NetId);
                if (playerIndex < 0)
                    continue;

                checkpoint.Players[playerIndex].Deck.Add(card.ToSerializable());
                changed++;
            }

            if (changed == 0)
                return;

            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            ModLog.Write($"Checkpoint deck recorded {changed} gained card(s): {reason}.");
        }
    }

    // 升级时只修改检查点里一张同 ID、同旧等级的牌。不会复制死亡时间线
    // 当前卡牌的附魔或整副牌组，因此升级前后的其他变化都不被误带回。
    public static void RecordCardUpgrade(CardModel card, int previousLevel)
    {
        if (card.CurrentUpgradeLevel <= previousLevel ||
            !TryGetPermanentDeckOwner(card, out var owner) ||
            !File.Exists(CheckpointPath) || !File.Exists(CheckpointVersionPath))
            return;

        lock (Sync)
        {
            if (!TryLoad(out var checkpoint))
                return;

            var savedPlayer = checkpoint.Players.FirstOrDefault(player => player.NetId == owner.NetId);
            if (savedPlayer is null)
                return;

            var savedCard = savedPlayer.Deck.FirstOrDefault(candidate =>
                candidate.Id == card.Id && candidate.CurrentUpgradeLevel == previousLevel)
                ?? savedPlayer.Deck
                    .Where(candidate => candidate.Id == card.Id &&
                                        candidate.CurrentUpgradeLevel < card.CurrentUpgradeLevel)
                    .OrderByDescending(candidate => candidate.CurrentUpgradeLevel)
                    .FirstOrDefault();
            if (savedCard is null)
            {
                ModLog.Write($"Checkpoint upgrade could not find {card.Id} at level {previousLevel}; change was not persisted.");
                return;
            }

            savedCard.CurrentUpgradeLevel = card.CurrentUpgradeLevel;
            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            ModLog.Write($"Checkpoint deck recorded upgrade: {card.Id} {previousLevel}->{card.CurrentUpgradeLevel}.");
        }
    }

    // 变形会通过 CardPileCmd.Add 把新牌加入 Deck；这里成对移除检查点中的
    // 原牌，避免被“获得卡牌”观察器误记成原牌 + 新牌。普通删牌不调用此方法。
    public static void RecordCardTransformations(
        IReadOnlyList<CheckpointCardTransformation> transformations,
        int successfulDeckAdds)
    {
        if (successfulDeckAdds <= 0 || transformations.Count == 0 ||
            !File.Exists(CheckpointPath) || !File.Exists(CheckpointVersionPath))
            return;

        lock (Sync)
        {
            if (!TryLoad(out var checkpoint))
                return;

            var removed = 0;
            foreach (var transformation in transformations.Take(successfulDeckAdds))
            {
                var savedPlayer = checkpoint.Players.FirstOrDefault(player =>
                    player.NetId == transformation.PlayerNetId);
                var original = savedPlayer?.Deck.FirstOrDefault(card =>
                    card.Id == transformation.OriginalId &&
                    card.CurrentUpgradeLevel == transformation.OriginalUpgradeLevel)
                    ?? savedPlayer?.Deck.FirstOrDefault(card => card.Id == transformation.OriginalId);
                if (savedPlayer is null || original is null)
                    continue;

                savedPlayer.Deck.Remove(original);
                removed++;
            }

            if (removed == 0)
                return;

            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            ModLog.Write($"Checkpoint deck paired {removed} transformed card(s) with their replacements.");
        }
    }
}
