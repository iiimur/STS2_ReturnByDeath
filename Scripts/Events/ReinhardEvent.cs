// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class ReinhardEventState
{
    private static int _endTurnCount;
    private static int _triggeredThisCombat;
    private static int _endTurnIntercepted;
    private static int _conversionPending;
    private static int _conversionApplying;
    private static int _conversionVisualActive;
    private static int _autoPlayInProgress;

    public static bool ConversionPending => Volatile.Read(ref _conversionPending) != 0;
    public static bool ConversionVisualActive => Volatile.Read(ref _conversionVisualActive) != 0;
    public static bool IsAutoPlayInProgress => Volatile.Read(ref _autoPlayInProgress) != 0;

    public static void MarkAutoPlayInProgress() => Volatile.Write(ref _autoPlayInProgress, 1);

    public static void ClearAutoPlayInProgress() => Volatile.Write(ref _autoPlayInProgress, 0);

    public static void ResetCombat()
    {
        Volatile.Write(ref _endTurnCount, 0);
        Volatile.Write(ref _triggeredThisCombat, 0);
        Volatile.Write(ref _endTurnIntercepted, 0);
        Volatile.Write(ref _conversionPending, 0);
        Volatile.Write(ref _conversionApplying, 0);
        Volatile.Write(ref _conversionVisualActive, 0);
    }

    public static bool TryInterceptEndTurn()
    {
        if (RouteState.IsIfRoute)
        {
            ModLog.Write("Reinhard event skipped because the run is on an IF route.");
            return false;
        }

        var endTurnNumber = Interlocked.Increment(ref _endTurnCount);
        ModLog.Write($"Reinhard end-turn tracker: end-turn #{endTurnNumber} registered.");

        if (Volatile.Read(ref _triggeredThisCombat) != 0)
            return false;

        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 0)
        {
            ModLog.Write($"Reinhard event skipped outside act 1: act={state?.CurrentActIndex.ToString() ?? "unknown"}.");
            return false;
        }

        // 第一层在火堆选择休息（HEAL）后即随休息检查点存档；历史记录是
        // 持久化的，读档后同样生效，休息过就不再触发莱茵哈鲁特事件。
        if (state.MapPointHistory.FirstOrDefault()?
                .Any(entry => entry.PlayerStats.Any(p => p.RestSiteChoices.Contains("HEAL"))) == true)
        {
            ModLog.Write("Reinhard event skipped: rested at a fire in act 1.");
            return false;
        }

        var player = state?.Players.FirstOrDefault(p =>
            p.Deck.Cards.Count(card => card is Guilty) >= 1 &&
            // 已经拥有莱茵哈鲁特时不再重复触发保命事件，避免重复生成同名卡。
            !p.Deck.Cards.Any(card => card is ReinhardCard));
        if (player is null)
            return false;

        var willDie = PredictEnemyTurnDeath(player);
        if (!willDie)
            return false;

        Interlocked.Exchange(ref _triggeredThisCombat, 1);
        Interlocked.Exchange(ref _conversionPending, 1);
        Volatile.Write(ref _endTurnIntercepted, 1);
        ModLog.Write("Reinhard event triggered before enemy turn: lethal intent predicted.");
        // 先播放事件专属音效，再进入莱茵哈鲁特的生成与自动出牌流程。
        // 音效失败不影响事件本身，避免素材读取问题阻断保命逻辑。
        if (CosmeticAudio.TryPlay("到此为止了.wav"))
            ModLog.Write("Played Reinhard event SFX: 到此为止了.wav.");
        _ = AddAndPlayAsync(player);
        return true;
    }

    public static bool ShouldBlockReadyToEndTurn =>
        Volatile.Read(ref _endTurnIntercepted) != 0;

    private static bool PredictEnemyTurnDeath(Player player)
    {
        var combatState = player.Creature.CombatState;
        if (combatState is null)
            return false;

        var enemies = combatState.Enemies
            .Where(enemy => enemy.IsAlive && enemy.Monster is not null)
            .ToArray();
        var predictedEnemyHp = enemies.ToDictionary(enemy => enemy, enemy => enemy.CurrentHp);
        var predictedEnemyBlock = enemies.ToDictionary(enemy => enemy, enemy => enemy.Block);

        // 先模拟玩家回合结束到敌人回合开始之间的原生效果：这些效果会改变
        // 玩家可承受伤害，或在敌人出手前直接杀死敌人。
        var predictedBlock = player.Creature.Block;
        var frostBlock = EstimateFrostOrbBlock(player);
        var platingBlock = EstimatePlatingBlock(player);
        predictedBlock += frostBlock + platingBlock;

        var constrictDamage = EstimateEndTurnSelfDamage(player);
        var enemyPoisonDamage = ApplyEnemyPoisonDamage(enemies, predictedEnemyHp, predictedEnemyBlock);
        var orbDamage = ApplyDamageOrbs(player, enemies, predictedEnemyHp, predictedEnemyBlock);
        var calendarDamage = ApplyStoneCalendar(player, enemies, predictedEnemyHp, predictedEnemyBlock);

        var incomingDamage = 0;
        foreach (var enemy in enemies)
        {
            // 敌人可能已被毒、闪电球、玻璃球或 Stone Calendar 在敌方回合前击杀。
            if (!predictedEnemyHp.TryGetValue(enemy, out var predictedHp) || predictedHp <= 0)
                continue;

            var monster = enemy.Monster!;
            if (!monster.IntendsToAttack || monster.NextMove is null)
                continue;

            foreach (var intent in monster.NextMove.Intents.OfType<AttackIntent>())
                incomingDamage += Math.Max(0, intent.GetTotalDamage(combatState.PlayerCreatures, enemy));
        }

        // Constrict 使用原版 Unpowered 伤害，仍然会先消耗玩家格挡，所以和敌人
        // 攻击合并到同一条 incomingDamage 中；玩家身上的 Poison 则在玩家下一次
        // 回合开始才触发，不属于本次“结束回合后是否会被敌人打死”的窗口。
        incomingDamage += constrictDamage;

        // 原版伤害流程会先消耗亡灵契约师自身格挡，随后 DieForYouPower
        // 把未被格挡的怪物攻击转给存活的 Osty；只有小手被打穿的余伤才会
        // 回到角色身上。因此预测致死时要把这段实际可承伤生命一并计算。
        var ostyHp = GetOstyAbsorbableHp(player);
        var effectiveHp = player.Creature.CurrentHp + predictedBlock + ostyHp;
        ModLog.Write($"Reinhard death prediction: incoming={incomingDamage}, playerHp={player.Creature.CurrentHp}, " +
            $"block={player.Creature.Block}+{frostBlock}+{platingBlock}, ostyHp={ostyHp}, " +
            $"selfEndDamage={constrictDamage}, enemyPoison={enemyPoisonDamage}, orbDamage={orbDamage}, " +
            $"calendarDamage={calendarDamage}, effectiveHp={effectiveHp}, enemies={combatState.Enemies.Count}.");
        return incomingDamage >= effectiveHp && incomingDamage > 0;
    }

    private static int EstimateFrostOrbBlock(Player player)
    {
        var total = 0;
        try
        {
            var orbs = player.PlayerCombatState?.OrbQueue?.Orbs;
            if (orbs is null)
                return 0;

            var goldPlatedCables = player.Relics
                .OfType<MegaCrit.Sts2.Core.Models.Relics.GoldPlatedCables>()
                .Any();
            var orbIndex = 0;
            foreach (var orb in orbs)
            {
                var triggerCount = goldPlatedCables && orbIndex == 0 ? 2 : 1;
                orbIndex++;
                if (orb is not FrostOrb)
                    continue;

                var block = Math.Max(0, (int)Math.Floor(orb.ModifyOrbValue(orb, orb.PassiveVal)));
                total += block * triggerCount;
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reinhard Frost orb prediction failed: {exception.Message}");
        }

        return total;
    }

    private static int EstimatePlatingBlock(Player player) =>
        player.Creature.Powers
            .OfType<PlatingPower>()
            .Sum(power => Math.Max(0, power.Amount));

    private static int EstimateEndTurnSelfDamage(Player player) =>
        // Constrict 的原生 AfterSideTurnEnd 使用 Unpowered 伤害，因而仍会被
        // Frost/Plating 等格挡吸收；这里保留原始伤害值并与攻击合并计算。
        player.Creature.Powers
            .OfType<ConstrictPower>()
            .Sum(power => Math.Max(0, power.Amount));

    private static int ApplyEnemyPoisonDamage(
        IReadOnlyList<Creature> enemies,
        Dictionary<Creature, int> predictedHp,
        Dictionary<Creature, int> predictedBlock)
    {
        var total = 0;
        foreach (var enemy in enemies)
        {
            var poison = enemy.Powers.OfType<PoisonPower>().FirstOrDefault();
            if (poison is null)
                continue;

            var damage = Math.Max(0, poison.CalculateTotalDamageNextTurn());
            total += damage;
            ApplyPredictedDamage(enemy, damage, predictedHp, predictedBlock, ignoreBlock: true);
        }

        return total;
    }

    private static int ApplyDamageOrbs(
        Player player,
        IReadOnlyList<Creature> enemies,
        Dictionary<Creature, int> predictedHp,
        Dictionary<Creature, int> predictedBlock)
    {
        var total = 0;
        try
        {
            var orbs = player.PlayerCombatState?.OrbQueue?.Orbs;
            if (orbs is null)
                return 0;

            // 金箔电缆会让本回合第一个球的被动额外触发一次；把这次额外
            // 触发纳入预判，避免低血量时少算一次冰球/伤害球。
            var goldPlatedCables = player.Relics
                .OfType<MegaCrit.Sts2.Core.Models.Relics.GoldPlatedCables>()
                .Any();
            var orbIndex = 0;

            // 闪电球会对一个当前可命中的敌人造成被动伤害，玻璃球则会对
            // 所有当前可命中的敌人造成被动伤害。黑球此时只增加蓄能值，
            // 原生不会在这个窗口直接造成伤害。
            foreach (var orb in orbs)
            {
                var triggerCount = goldPlatedCables && orbIndex == 0 ? 2 : 1;
                orbIndex++;
                if (orb is FrostOrb)
                    continue;
                if (orb is not LightningOrb and not GlassOrb)
                    continue;

                var damage = Math.Max(0, (int)Math.Floor(orb.ModifyOrbValue(orb, orb.PassiveVal)));
                for (var trigger = 0; trigger < triggerCount && damage > 0; trigger++)
                {
                    if (orb is GlassOrb)
                    {
                        foreach (var target in enemies.Where(enemy =>
                                     enemy.IsHittable &&
                                     predictedHp.TryGetValue(enemy, out var hp) && hp > 0))
                        {
                            ApplyPredictedDamage(target, damage, predictedHp, predictedBlock, ignoreBlock: false);
                            total += damage;
                        }
                    }
                    else
                    {
                        var target = enemies.FirstOrDefault(enemy =>
                            enemy.IsHittable &&
                            predictedHp.TryGetValue(enemy, out var hp) && hp > 0);
                        if (target is null)
                            break;

                        ApplyPredictedDamage(target, damage, predictedHp, predictedBlock, ignoreBlock: false);
                        total += damage;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reinhard damage-orb prediction failed: {exception.Message}");
        }

        return total;
    }

    private static int ApplyStoneCalendar(
        Player player,
        IReadOnlyList<Creature> enemies,
        Dictionary<Creature, int> predictedHp,
        Dictionary<Creature, int> predictedBlock)
    {
        try
        {
            var calendar = player.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.StoneCalendar>()
                .FirstOrDefault();
            var combatState = player.Creature.CombatState;
            if (calendar is null || combatState is null || player.PlayerCombatState is null)
                return 0;

            var damageTurn = calendar.DynamicVars["DamageTurn"].IntValue;
            if (damageTurn != player.PlayerCombatState.TurnNumber)
                return 0;

            var damage = Math.Max(0, calendar.DynamicVars["Damage"].IntValue);
            var targetCount = 0;
            foreach (var enemy in enemies)
            {
                if (enemy.IsHittable && predictedHp.TryGetValue(enemy, out var hp) && hp > 0)
                {
                    ApplyPredictedDamage(enemy, damage, predictedHp, predictedBlock, ignoreBlock: false);
                    targetCount++;
                }
            }

            return damage * targetCount;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reinhard Stone Calendar prediction failed: {exception.Message}");
            return 0;
        }
    }

    private static void ApplyPredictedDamage(
        Creature target,
        int damage,
        Dictionary<Creature, int> predictedHp,
        Dictionary<Creature, int> predictedBlock,
        bool ignoreBlock)
    {
        if (damage <= 0 || !predictedHp.TryGetValue(target, out var hp) || hp <= 0)
            return;

        if (!ignoreBlock && predictedBlock.TryGetValue(target, out var block) && block > 0)
        {
            var blocked = Math.Min(block, damage);
            predictedBlock[target] = block - blocked;
            damage -= blocked;
        }

        predictedHp[target] = Math.Max(0, hp - damage);
    }

    private static int GetOstyAbsorbableHp(Player player)
    {
        // Player.Osty 会在小手离场或死亡后保留实例，所以必须先检查 IsOstyAlive。
        // DieForYouPower 是本地版本中唯一会把亡灵契约师承受的伤害转移给小手的能力。
        if (!player.IsOstyAlive || player.Osty is not { } osty ||
            !osty.Powers.Any(power => power is DieForYouPower))
        {
            return 0;
        }

        return Math.Max(0, osty.CurrentHp);
    }

    private static async Task AddAndPlayAsync(Player player)
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState()
                ?? throw new InvalidOperationException("RunState is unavailable for the Reinhard event.");
            var combatState = player.Creature.CombatState
                ?? throw new InvalidOperationException("CombatState is unavailable for the Reinhard event.");

            // 战斗中生成的卡必须由 CombatState.CreateCard 创建。RunState.CreateCard
            // 只会登记到整局存档；手牌虽然能显示，但原版在打出/移入结果牌堆时
            // 会发现 CombatState.ContainsCard(card) 为 false，随后抛出异常。
            var generatedCard = combatState.CreateCard<ReinhardCard>(player);
            var addResult = await CardPileCmd.AddGeneratedCardToCombat(
                generatedCard, PileType.Hand, player, CardPilePosition.Random);
            await Task.Yield();

            // AddGeneratedCardToCombat 可能会把传入实例包装/替换成真正登记在战斗状态里的实例。
            // 必须使用返回结果中的 cardAdded，否则手牌 UI 虽然能显示，手动打出时
            // CardPileCmd.AddDuringManualCardPlay 会因 CombatState 不包含该实例而报错。
            var resultField = addResult.GetType().GetField(
                "cardAdded", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var resultCard = resultField?.GetValue(addResult) as CardModel;
            var reinhard = resultCard ?? player.Piles
                .FirstOrDefault(pile => pile.Type == PileType.Hand)?
                .Cards.LastOrDefault(card => card is ReinhardCard);
            if (reinhard is null)
                throw new InvalidOperationException("Reinhard card was not found in hand after generation.");

            ModLog.Write($"Reinhard generated card state: pile={reinhard.Pile?.Type}, " +
                $"inCombat={reinhard.IsInCombat}, combatState={(reinhard.CombatState is not null)}, " +
                $"sameAsInput={ReferenceEquals(reinhard, generatedCard)}.");

            // PlayerChoiceContext 是抽象类，原版自动行动使用的是这个具体的 hook 上下文。
            // 用当前卡牌和战斗状态构造 Combat 类型上下文，既能执行 AttackCommand，
            // 也不会把一个假的上下文交给原版动作队列。
            combatState = reinhard.CombatState
                ?? throw new InvalidOperationException("Generated Reinhard card has no CombatState.");
            var context = new HookPlayerChoiceContext(
                reinhard, 0UL, combatState, GameActionType.Combat);

            // 自动出牌期间置标记：卡牌伤害动画结束后，存活的怪物会被强制
            // 击杀（见 ReinhardCardPlayback），防止拦截的回合流程卡死。
            ReinhardEventState.MarkAutoPlayInProgress();
            try
            {
                await CardCmd.AutoPlay(
                    context, reinhard, player.Creature, AutoPlayType.Default, false, false);
            }
            finally
            {
                ReinhardEventState.ClearAutoPlayInProgress();
            }
            ModLog.Write("Reinhard card generated and auto-played with native card-play flow.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reinhard event card play failed: {exception}");
        }
    }

    public static async Task ConvertGuiltyAfterCombatAsync(Task settlementTask)
    {
        try
        {
            // 先完整等待原版战斗结算。此前在结算任务完成前就开始变牌，
            // 结算页随后出现会把正在播放的变牌动画覆盖掉。
            await settlementTask;

            // 结算完成后再让出一帧，确保原版结算界面已经完成切换，
            // 然后播放中央的变牌动画。
            await Task.Yield();
            if (Interlocked.Exchange(ref _conversionApplying, 1) != 0 ||
                Volatile.Read(ref _conversionPending) == 0)
                return;

            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state is null)
                return;

            var converted = 0;
            foreach (var player in state.Players)
            {
                var guilty = player.Deck.Cards.Where(card => card is Guilty).ToArray();
                if (guilty.Length == 0)
                    continue;

                // 使用原版 CardCmd.Transform：它会负责从原牌堆移除、按原位置
                // 放入替换卡，并播放 NCardTransformVfx 的完整变牌动画。
                foreach (var guiltyCard in guilty)
                {
                    var replacement = state.CreateCard<ReinhardCard>(player);
                    replacement.CombatsSeen = guiltyCard is Guilty guiltyCardModel
                        ? guiltyCardModel.CombatsSeen
                        : 0;
                    Volatile.Write(ref _conversionVisualActive, 1);
                    try
                    {
                        var result = await CardCmd.Transform(
                            guiltyCard,
                            replacement,
                            // None 会连同原版 NCardTransformVfx 一起跳过，导致动画消失。
                            // EventLayout 使用事件页容器，视觉中心会偏移；
                            // HorizontalLayout 保留原版变牌特效，并使用全局横向预览容器。
                            CardPreviewStyle.HorizontalLayout);
                        if (result.HasValue && result.Value.success)
                            converted++;
                    }
                    finally
                    {
                        Volatile.Write(ref _conversionVisualActive, 0);
                    }
                }
            }

            await SaveManager.Instance.SaveRun(state.CurrentRoom, true);
            Volatile.Write(ref _conversionPending, 0);
            ModLog.Write($"Reinhard event converted {converted} Guilty cards after combat.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _conversionApplying, 0);
            ModLog.Write($"Reinhard Guilty conversion failed: {exception}");
        }
    }

}

// 结算页的覆盖层可能在变牌 VFX 之后绘制。仅提升本 mod 的愧疚转换特效，
// 避免影响游戏中其他正常变牌动画的图层顺序。
[HarmonyPatch(typeof(NCardTransformVfx), "_Ready")]
internal static class ReinhardTransformVfxLayerPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCardTransformVfx __instance)
    {
        if (!ReinhardEventState.ConversionVisualActive)
            return;

        // 结算奖励窗口位于普通全局预览容器之上，单独提高 ZIndex 仍可能被
        // 另一个 CanvasLayer 覆盖。移到原版专门的顶层 VFX 容器，并保留
        // 当前全局坐标，因此不会改变已经正确的屏幕居中位置。
        var aboveTopBarVfx = NRun.Instance?.GlobalUi?.AboveTopBarVfxContainer;
        if (aboveTopBarVfx is not null && !aboveTopBarVfx.IsAncestorOf(__instance))
            __instance.Reparent(aboveTopBarVfx, true);

        // Godot CanvasItem 的合法 ZIndex 上限是 4095；10000 会触发
        // RenderingServer 错误，并可能干扰结算页/房间切换时的渲染。
        __instance.ZIndex = 4095;
        __instance.ZAsRelative = false;
        __instance.ShowBehindParent = false;
        ModLog.Write("Raised Guilty-to-Reinhard transform VFX above the settlement overlay.");
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "SetUpCombat")]
internal static class ReinhardCombatSetupPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        ReinhardEventState.ResetCombat();
        OttoCardLifecycle.ResetCombat();
    }
}

// Otto 在原版首回合发牌前加入战斗手牌，避免其只能依赖普通抽牌。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "SetReadyToEndTurn")]
internal static class ReinhardEndTurnReadyPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !ReinhardEventState.ShouldBlockReadyToEndTurn;
}

// 这是原版结束回合按钮按下时立即调用的入口；在这里拦截可以避免
// 玩家行动被禁用，也不会让敌方回合开始。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), nameof(MegaCrit.Sts2.Core.Combat.CombatManager.OnEndedTurnLocally))]
internal static class ReinhardEndTurnButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !ReinhardEventState.TryInterceptEndTurn();
}

[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
internal static class ReinhardCombatSettlementPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        if (ReinhardEventState.ConversionPending)
            __result = ReinhardEventState.ConvertGuiltyAfterCombatAsync(__result);
    }
}
