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
            // 新的存档点即新的诅咒预算锚点：a=0，b=0。
            CurseBudget.Reset();
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

    // 强欲之心的序列化遗物 ID（ModelId 是 record，直接按值比较）。
    private static readonly ModelId HeartOfGreedId = new("RELIC", "HEART_OF_GREED");

    // 强欲之心是否在场：运行时遗物列表与检查点序列化遗物都查一遍，
    // 兼容回归过程中任一时机的调用。
    private static bool HoldsHeartOfGreed(Player player, SerializablePlayer savedPlayer) =>
        player.Relics.Any(relic => relic is HeartOfGreed) ||
        savedPlayer.Relics.Any(relic => relic.Id == HeartOfGreedId);

    public static async Task ApplyRecoveryState(RunState state, SerializableRun checkpoint)
    {
        var count = Math.Min(state.Players.Count, checkpoint.Players.Count);
        // 本次死亡是否要推进诅咒预算；强欲线清零后不推进。
        var advanceBudget = false;
        for (var i = 0; i < count; i++)
        {
            var player = state.Players[i];
            var savedPlayer = checkpoint.Players[i];

            // 傲慢线的旧特权（回归不加诅咒+血量与上限各+1）已删除：进入傲慢
            // 线时改为授予“傲慢之心”，诅咒照常叠加但可被打出——不再与强欲
            // 线的回归加成重复。
            // 强欲之心：每次回归血量与血量上限各 +2（描述只谈上限，是因为
            // 原生加上限时会同步加当前生命；这里直接把两者都写进检查点）。
            // 加成同时写回检查点，多次回归持续累计。
            var holdsHeartOfGreed = HoldsHeartOfGreed(player, savedPlayer);
            var hpBonus = holdsHeartOfGreed ? 2 : 0;
            savedPlayer.MaxHp += hpBonus;
            savedPlayer.CurrentHp += hpBonus;

            // Set the runtime value after saved-run setup as well as preserving
            // the serialized value, so max HP gains survive the return.
            player.Creature.SetMaxHpInternal(savedPlayer.MaxHp);
            if (AmnesiaState.TimelineHidden)
            {
                // 失忆回归：卡组重置为开局初始状态，且不添加回归诅咒。
                // 失忆线仍按剧情规则静默重建初始卡组。
                await ResetDeckToInitialAsync(player);
                advanceBudget = true;
                ModLog.Write("Amnesia recovery: deck reset to the initial state; no curse added.");
            }
            else if (holdsHeartOfGreed)
            {
                // 强欲之心：不再愧疚——本次回归不加任何诅咒，预算清零并保持
                // 0（顶栏显示“愧疚 0 受伤 0”，之后的死亡也不再增长）。
                CurseBudget.ClearToZero("Heart of Greed recovery");
                ModLog.Write("Heart of Greed recovery: no curse added; HP and max HP increased by 2.");
            }
            else
            {
                // 诅咒预算：A/B 直接就是“本次死亡施加的数量”。继承牌组里
                // 已有此前回归加入的诅咒，新诅咒直接叠加上去——第 1 次死亡
                // +1、第 2 次 +2、第 3 次 +3+1 受伤……施加数量只由死亡次数
                // 决定，无法刷改。施加完成后才推进到下一次（见下方）。
                var (guilties, injuries) = CurseBudget.Current;
                for (var guilt = 0; guilt < guilties; guilt++)
                    await AddCurseAsync<Guilty>(player, state);
                for (var inj = 0; inj < injuries; inj++)
                    await AddCurseAsync<Injury>(player, state);
                advanceBudget = true;
                ModLog.Write($"Recovery curses applied from the death budget: guilty x{guilties}, injury x{injuries}.");
            }
            // 回归血量使用检查点保存的 CurrentHp（傲慢线已含 +1 加成）。
            player.Creature.SetCurrentHpInternal(savedPlayer.CurrentHp);
            ModLog.Write($"Recovery health restored from checkpoint: {savedPlayer.CurrentHp}/{savedPlayer.MaxHp}.");
        }

        // 先施加诅咒，再把预算推进到下一条时间线。
        if (advanceBudget)
            CurseBudget.AdvanceAfterDeath();

        if (checkpoint.Players.Any(saved => saved.Relics.Any(relic => relic.Id == HeartOfGreedId)))
        {
            // 把回归加成写回检查点文件：下一次死亡回归以新的血量基准继续累计。
            try
            {
                File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
                ModLog.Write("Recovery HP bonus written back to the checkpoint.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Could not write the recovery HP bonus back to the checkpoint: {exception.Message}");
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

        // 简单继承：死亡时的最新卡组直接覆盖检查点牌组（含此前回归加入的
        // 诅咒——新诅咒按预算叠加施加，见 ApplyRecoveryState）。
        CopyDeathDeckToCheckpoint(deathState, checkpoint);
        AmnesiaState.CaptureDeathEpisode(checkpoint, deathState);
        // 诅咒预算：先计算后施加。a++、b += a/3；回归时按 a/b 施加诅咒。
        // 持有强欲之心时愧疚值固定为 0：死亡不再累计预算（回归时会保持清零）。
        // 其余情况不在此推进：本次死亡的施加数量已存于预算中，待回归真正
        // 施加后再推进（见 ApplyRecoveryState 的 AdvanceAfterDeath）。
        if (deathState.Players.Any(player => player.Relics.Any(relic => relic.Id == HeartOfGreedId)))
        {
            CurseBudget.ClearToZero("Heart of Greed held at death");
            ModLog.Write("Heart of Greed held at death; curse budget cleared.");
        }
        CopyPersistentNonDeckState(deathState, checkpoint);

        try
        {
            File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
            try { File.Delete(LegacyPendingRestoredCardsPath); } catch { }
            ModLog.Write("Checkpoint deck replaced with the deck at death time; persistent state merged.");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Death-state merge failed: {exception}");
            return false;
        }
    }

    private static void CopyDeathDeckToCheckpoint(SerializableRun deathState, SerializableRun checkpoint)
    {
        var count = Math.Min(deathState.Players.Count, checkpoint.Players.Count);
        for (var i = 0; i < count; i++)
        {
            checkpoint.Players[i].Deck = deathState.Players[i].Deck.ToList();
            ModLog.Write($"Checkpoint deck inherited from death state: {checkpoint.Players[i].Deck.Count} cards.");
        }
    }

    // 诅咒预算：以最新存档点为锚点。A/B 直接表示“下一次死亡将要施加的
    // 愧疚/受伤数量”——存档点捕获后为（a=1，b=0），因此下一次死亡固定
    // 为 1 张愧疚。回归时先按当前 A/B 施加诅咒，再推进到下一次
    // （a++、b += a/3）。施加数量只由死亡次数决定，无法通过死亡前的
    // 牌组操作刷改；顶栏显示因此可以直接读取 A/B 而无需换算。
    internal static class CurseBudget
    {
        private sealed class BudgetFile
        {
            // 2 = 新语义（A/B 为下一次死亡施加的数量，锚点 a=1）；0 = 旧语义
            // （A/B 为已施加的数量，锚点 a=0）。
            public int Version { get; set; }
            public int A { get; set; }
            public int B { get; set; }
            // 强欲线（强欲之心）下置位：此后即使捕获新存档点也保持 a=b=0。
            public bool Suppressed { get; set; }
        }

        private const int CurrentVersion = 2;

        private static readonly object Sync = new();
        private static readonly string BudgetPath = Path.Combine(
            RecoveryMarker.ModDirectory,
            "return-by-death.curse-budget.json");
        private static BudgetFile? _file;

        private static BudgetFile EnsureLoaded()
        {
            if (_file is not null)
                return _file;
            try
            {
                _file = JsonSerializer.Deserialize<BudgetFile>(File.ReadAllText(BudgetPath)) ?? new BudgetFile();
            }
            catch (Exception exception)
            {
                ModLog.Write($"Curse budget load failed; starting from the first-death value: {exception.Message}");
                _file = new BudgetFile { Version = CurrentVersion, A = 1, B = 0 };
                return _file;
            }

            // 旧语义迁移：旧 A/B 是“已施加的数量”，旧的顶栏显示为
            // （A+1, B+(A+1)/3）；新语义把同一组数字直接存成待施加数量。
            if (_file.Version < CurrentVersion)
            {
                var oldA = _file.A;
                var oldB = _file.B;
                _file.A = oldA + 1;
                _file.B = oldB + (oldA + 1) / 3;
                _file.Version = CurrentVersion;
                Save();
                ModLog.Write($"Curse budget migrated to the new semantics: a={_file.A}, b={_file.B}.");
            }
            return _file;
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(BudgetPath, JsonSerializer.Serialize(EnsureLoaded()));
            }
            catch (Exception exception)
            {
                ModLog.Write($"Curse budget save failed: {exception.Message}");
            }
        }

        // 新锚点（存档点捕获）：下一次死亡施加 1 张愧疚；强欲线下保持清零。
        public static void Reset(string reason = "checkpoint capture")
        {
            lock (Sync)
            {
                var file = EnsureLoaded();
                if (!file.Suppressed)
                {
                    file.A = 1;
                    file.B = 0;
                }
                Save();
                ModLog.Write(file.Suppressed
                    ? $"Curse budget anchor captured under the Greed suppression ({reason}): a={file.A}, b={file.B}."
                    : $"Curse budget reset ({reason}): a=1, b=0.");
            }
        }

        // 新开局：清除抑制标记，恢复到普通的首次死亡预算。
        public static void ResetForNewRun()
        {
            lock (Sync)
            {
                var file = EnsureLoaded();
                file.Suppressed = false;
                file.A = 1;
                file.B = 0;
                Save();
            }
            ModLog.Write("Curse budget reset for a new run: a=1, b=0 (suppression cleared).");
        }

        // 强欲线：预算固定为 0，死亡不再施加任何诅咒，顶栏显示“愧疚 0 受伤 0”。
        public static void ClearToZero(string reason)
        {
            lock (Sync)
            {
                var file = EnsureLoaded();
                file.A = 0;
                file.B = 0;
                file.Suppressed = true;
                Save();
            }
            ModLog.Write($"Curse budget cleared to zero ({reason}): a=0, b=0.");
        }

        // 本次死亡的诅咒已施加完毕，推进到下一次的预算（a++、b += a/3）。
        public static void AdvanceAfterDeath()
        {
            lock (Sync)
            {
                var file = EnsureLoaded();
                if (file.Suppressed)
                    return;
                file.A++;
                file.B += file.A / 3;
                Save();
                ModLog.Write($"Curse budget advanced for the next death: a={file.A}, b={file.B}.");
            }
        }

        public static (int Guilties, int Injuries) Current
        {
            get
            {
                lock (Sync)
                {
                    var file = EnsureLoaded();
                    return (file.A, file.B);
                }
            }
        }
    }

    // 按预算施加一张诅咒卡：优先走原生加牌动画，失败时静默入组兜底。
    private static async Task AddCurseAsync<TCard>(Player player, RunState state)
        where TCard : CardModel
    {
        var curse = state.CreateCard<TCard>(player);
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
        // 新开局：清除强欲抑制，诅咒预算回到首次死亡的锚点。
        CurseBudget.ResetForNewRun();
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

    // 艾姬多娜事件的试验遗物（过去的苦痛/现在的牺牲/未来的骨骸）与强欲之心：
    // 获得即写入最新检查点，因此死亡回归后依然保留，不随时间线回滚消失。
    // 其余遗物仍遵循检查点快照规则（回归时回到存档点时的遗物列表）。
    public static void RecordEchidnaRelic(RelicModel relic, Player owner)
    {
        if (relic is not (PainOfThePast or SacrificeOfThePresent or BonesOfTheFuture or HeartOfGreed or HeartOfSloth or HeartOfPride or HeartOfWrath or OttoContract) ||
            !File.Exists(CheckpointPath) || !File.Exists(CheckpointVersionPath))
            return;

        switch (relic)
        {
            case PainOfThePast:
                IfAchievements.Unlock("echidna_past");
                break;
            case SacrificeOfThePresent:
                IfAchievements.Unlock("echidna_present");
                break;
            case BonesOfTheFuture:
                IfAchievements.Unlock("echidna_future");
                break;
        }

        lock (Sync)
        {
            if (!TryLoad(out var checkpoint))
                return;

            var playerIndex = checkpoint.Players.FindIndex(player => player.NetId == owner.NetId);
            if (playerIndex < 0)
                return;

            var savedPlayer = checkpoint.Players[playerIndex];
            if (savedPlayer.Relics.Any(saved => saved.Id == relic.Id))
                return;

            savedPlayer.Relics.Add(relic.ToSerializable());
            try
            {
                File.WriteAllText(CheckpointPath, JsonSerializationUtility.ToJson(checkpoint));
                ModLog.Write($"Echidna relic persisted into checkpoint: {relic.Id}.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Echidna relic checkpoint write failed: {exception.Message}");
            }
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
