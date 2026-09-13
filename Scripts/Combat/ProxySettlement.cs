// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class SlothCombatSettlement
{
    public static bool TryStart(CombatRoom room, out Task result)
    {
        result = Task.CompletedTask;
        try
        {
            result = StartPreFinishedCombat(room);
            return true;
        }
        catch (Exception exception)
        {
            // 反射或游戏版本变动时绝不阻断原战斗，记录后退回原版流程。
            ModLog.Write($"Sloth/proxy immediate settlement could not start; falling back to native combat: {exception}");
            return false;
        }
    }

    private static Task StartPreFinishedCombat(CombatRoom room)
    {
        var startPreFinished = AccessTools.Method(
            typeof(CombatRoom), "StartPreFinishedCombat", Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(CombatRoom).FullName, "StartPreFinishedCombat");
        return startPreFinished.Invoke(room, null) as Task
            ?? throw new InvalidOperationException("Native StartPreFinishedCombat did not return a Task.");
    }

    internal static async Task ApplyProxyHealthThenSettleAsync(CombatRoom room, int hpLoss)
    {
        // StartCombatInternal 是从 SetUpCombat 内部启动的；先让出一次执行权，
        // 让 CombatRoom 完成 AfterCombatRoomLoaded，确保受击节点已经可见。
        await Task.Yield();
        var player = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault();
        if (player is not null && hpLoss > 0)
        {
            var before = player.Creature.CurrentHp;
            var targetHp = Math.Clamp(before - hpLoss, 0, player.Creature.MaxHp);
            // 此方法从 CombatManager.StartCombatInternal 入口执行：战斗状态与
            // 角色节点已经建立，但首回合尚未开始。使用原生不可格挡、不可被
            // 力量修正的伤害，保留受击动画以及完整的死亡/复活/回归流程。
            try
            {
                await CreatureCmd.Damage(
                    new BlockingPlayerChoiceContext(),
                    player.Creature,
                    hpLoss,
                    ValueProp.Unblockable | ValueProp.Unpowered,
                    player.Creature);
            }
            catch (Exception exception)
            {
                // 完整 CombatState 下通常不会进入这里；保底时只补到目标血量，
                // 已经扣掉的部分不会重复计算。致死则仍交给原生 Kill 死亡链。
                ModLog.Write($"Proxy native damage failed; applying guarded fallback: {exception}");
                if (player.Creature.CurrentHp > targetHp)
                    player.Creature.SetCurrentHpInternal(targetHp);
                if (targetHp <= 0 && !player.Creature.IsDead)
                    await CreatureCmd.Kill(player.Creature, true);
            }
            ModLog.Write($"Proxy damage animation completed: requested={hpLoss}, " +
                $"hp={before}->{player.Creature.CurrentHp}/{player.Creature.MaxHp}.");

            var combatManager = CombatManager.Instance;
            if (player.Creature.IsDead || player.Creature.CurrentHp <= 0 ||
                RunManager.Instance?.IsGameOver == true || combatManager.IsAboutToLose)
            {
                // CreatureCmd.Damage 已经通过原生 Kill/HandlePlayerDeath 链启动死亡。
                // 此时绝不能再创建奖励页，否则会与死亡回归同时操作房间状态。
                ModLog.Write("Proxy damage triggered death; skipped combat settlement and yielded to the native death flow.");
                return;
            }
        }
        else if (player is not null && hpLoss < 0)
        {
            var before = player.Creature.CurrentHp;
            await CreatureCmd.Heal(player.Creature, -hpLoss, true);
            ModLog.Write($"Proxy healing animation completed: requested={-hpLoss}, " +
                $"hp={before}->{player.Creature.CurrentHp}/{player.Creature.MaxHp}.");
        }

        // 原版战斗结束（CombatManager.EndCombatInternal）会触发
        // Hook.AfterCombatEnd，愧疚等按场次结算的卡牌在这里推进计数（满 5 场
        // 自动移出牌组）。代理结算跳过了真实战斗，不会走到原版结束链，这里
        // 手动补发同一钩子，保持“代理一场 = 实际打完一场”的语义。死亡时仍
        // 走原生死亡回归流程，不计入（与真实战斗死亡一致）。
        var combatState = room.CombatState;
        if (combatState is not null)
        {
            await MegaCrit.Sts2.Core.Hooks.Hook.AfterCombatEnd(
                combatState.RunState, combatState, room);
        }

        // 代理路径在原生 StartCombat 之后接管：此时怪物已经生成、ActiveCombat
        // 房间已经创建。不能直接调用原生 StartPreFinishedCombat——它第一步固定
        // 调用 GenerateMonstersWithSlots，对已生成的遭遇会抛 InvalidOperationException，
        // 异常会炸掉整个回合循环（游戏端只进 Sentry，战斗永久卡死）。
        // 这里按原生逻辑内联复刻结算，仅在怪物尚未生成时才补生成。
        var encounter = room.Encounter;
        if (!encounter.HaveMonstersBeenGenerated)
            encounter.GenerateMonstersWithSlots(room.CombatState.RunState);
        await PreloadManager.LoadRoomCombatAssets(encounter, room.CombatState.RunState);
        var finishedRoom = NCombatRoom.Create(room, CombatRoomMode.FinishedCombat);
        NRun.Instance?.SetCurrentRoom(finishedRoom);
        finishedRoom?.SetUpBackground(room.CombatState.RunState);
        NMapScreen.Instance?.SetTravelEnabled(enabled: true);
        foreach (var settledPlayer in room.CombatState.RunState.Players)
            settledPlayer.ResetCombatState();
        RunManager.Instance?.ActionExecutor.Unpause();
        if (encounter.ShouldGiveRewards)
        {
            await room.OfferRoomEndRewards();
        }
        else if (finishedRoom is not null)
        {
            await finishedRoom.Ui.ProceedWithoutRewards();
        }
        ModLog.Write("Proxy combat settled into the native pre-finished reward flow.");
    }
}

// 代理战斗先让 CombatRoom 走完原版 SetUpCombat，再在首回合开始前接管。
// 这样 CreatureCmd 能获得完整 CombatState 并播放受击动画、触发原生死亡链。
internal static class ProxyCombatSettlementPending
{
    private static readonly object Sync = new();
    private static CombatRoom? _room;
    private static int _hpLoss;

    public static void Arm(CombatRoom room, int hpLoss)
    {
        lock (Sync)
        {
            _room = room;
            _hpLoss = hpLoss;
        }
    }

    public static bool TryConsume(out CombatRoom room, out int hpLoss)
    {
        lock (Sync)
        {
            if (_room is null)
            {
                room = null!;
                hpLoss = 0;
                return false;
            }

            room = _room;
            hpLoss = _hpLoss;
            _room = null;
            _hpLoss = 0;
            return true;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _room = null;
            _hpLoss = 0;
        }
    }
}

[HarmonyPatch(typeof(CombatRoom), "StartCombat")]
internal static class SlothCombatSettlementPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CombatRoom __instance, ref Task __result)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is not null)
            EncounterJournalStore.BeginCombatTracking(state, __instance.Encounter);
        var shouldSkipReplayEncounter = !SlothRouteRules.IsActive &&
            ProxyModeState.Enabled && state is not null &&
            EncounterJournalStore.ShouldSkipEncounter(state, __instance.Encounter);
        if ((!SlothRouteRules.IsActive && !shouldSkipReplayEncounter) ||
            ArchitectFinaleState.IsArchitectEncounter(__instance.Encounter))
        {
            ProxyCombatSettlementPending.Clear();
            return true;
        }

        var proxyDamage = shouldSkipReplayEncounter
            ? EncounterJournalStore.GetCurrentEncounterHpLoss()
            : 0;
        if (shouldSkipReplayEncounter)
        {
            EncounterJournalStore.MarkCurrentEncounterSkipped();
            ProxyCombatSettlementPending.Arm(__instance, proxyDamage);
            ModLog.Write($"Proxy mode armed native pre-turn health settlement; historical net HP loss={proxyDamage}.");
            // 继续原版 CombatRoom.StartCombat，使 CombatManager 先创建完整
            // CombatState；下面的 StartCombatInternal 补丁会在首回合前接管。
            return true;
        }

        ProxyCombatSettlementPending.Clear();
        if (!SlothCombatSettlement.TryStart(__instance, out var settlementTask))
            return true;

        __result = settlementTask;
        ModLog.Write("Sloth route skipped combat and entered native pre-finished settlement.");
        return false;
    }
}

[HarmonyPatch(typeof(CombatManager), "StartCombatInternal")]
internal static class ProxyCombatPreTurnSettlementPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        if (!ProxyCombatSettlementPending.TryConsume(out var room, out var hpLoss))
            return true;

        __result = SlothCombatSettlement.ApplyProxyHealthThenSettleAsync(room, hpLoss);
        return false;
    }
}

// 记录每场正常完成的遭遇实际净生命变化（含最新一次），代理模式取同一遭遇最近一次的净损失。
// 预完成战斗（怠惰/代理）不会写入新的 0 伤害，避免覆盖真实历史。
[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
internal static class EncounterDamageHistoryPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result) =>
        __result = RecordAfterSettlementAsync(__result);

    private static async Task RecordAfterSettlementAsync(Task settlementTask)
    {
        await settlementTask;
        EncounterJournalStore.RecordCurrentEncounterDamage();
    }
}

// 标准事件选项的文本与效果由这两个构造器共用。进入怠惰线后，原版会自然
// 渲染为“失去 0 点生命/最大生命，获得……”，而奖励部分完全保留。
