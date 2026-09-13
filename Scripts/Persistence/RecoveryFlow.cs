// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class RecoveryFlow
{
    private static int _isRestoring;

    public static bool TryStart() => Interlocked.CompareExchange(ref _isRestoring, 1, 0) == 0;

    public static async Task RestoreCheckpointAsync(SerializableRun checkpoint, bool playRecoveryAudio)
    {
        // 回归前记录游玩时间；恢复后把被检查点回退掉的秒数计入真实总时长。
        var playtimeBeforeRestore = RunManager.Instance.RunTime;
        // 失忆回归：第三层第一次死亡触发的特殊回归。
        var isAmnesiaRecovery = AmnesiaState.ConsumeRecoveryPending();
        try
        {
            ModLog.Write($"Restoring act {checkpoint.CurrentActIndex} checkpoint.");
            // 回归后房间序号重置，旧时间线的奖励快照必须一并丢弃。
            RewardSnapshotStore.Clear();
            // 奥托战斗结算的待存档标记随旧时间线作废。
            OttoSettlementCheckpoint.Clear();
            RunManager.Instance.CleanUp(false);
            // 失忆回归是一条全新时间线：检查点带回的 RunTime 与累计的回归
            // 丢失量都属于失忆前的旧时间线，全部丢弃，总时间从零重新计时。
            if (isAmnesiaRecovery)
            {
                checkpoint.RunTime = 0;
                TruePlaytimeTracker.Reset();
            }
            var restoredState = RunState.FromSerializable(checkpoint);
            await RunManager.Instance.SetUpSavedSingleplayer(restoredState, checkpoint);
            // 失忆时间线：不开启遭遇重放（预告隐藏、遭遇不记录）。
            EncounterJournalStore.SetReplayActive(!isAmnesiaRecovery);
            var game = NGame.Instance
                ?? throw new InvalidOperationException("NGame is unavailable during checkpoint recovery.");
            // 检查点若落在已休息的火堆（历史记录带 HEAL），恢复后直接进入
            // “只剩前进按钮”的完成态；标志必须在房间节点创建之前设置。
            // 该结果同时作为蕾姆事件的触发标记：LoadRun 会为当前房间追加一条
            // 不带 HEAL 的新历史条目，之后无法再从历史记录反推。
            var recoveredToRestedFire = restoredState.CurrentMapPointHistoryEntry?
                .PlayerStats.Any(p => p.RestSiteChoices.Contains("HEAL")) == true;
            RouteState.SetAtRestedFireCheckpoint(recoveredToRestedFire);
            if (recoveredToRestedFire)
            {
                RestSiteRecovery.MarkPending();
            }
            await game.LoadRun(restoredState, checkpoint.PreFinishedRoom);
            // 原版 RunTime 此时已回到检查点的值；差额即本次回归丢失的游玩时间。
            // 失忆时间线的时间不计入总时间，因此失忆回归不累计。
            if (!isAmnesiaRecovery)
                TruePlaytimeTracker.AddLostSeconds(playtimeBeforeRestore - checkpoint.RunTime);
            if (isAmnesiaRecovery)
            {
                // 进入失忆时间线：遭遇预告、地图记录与总时间全部隐藏；
                // 同时保存“存档前”的作战记录快照供记忆面板显示。
                AmnesiaState.SetTimelineHidden(true);
                AmnesiaState.CaptureHistorySnapshot(checkpoint);
                // 失忆：清空运行状态与检查点里的已走节点，本次回归结束时的
                // 原生存档也不再包含它们；逐层历史保留（火堆“已完成”状态等
                // 原生逻辑依赖），检查点文件与记忆面板的历史快照不受影响。
                ClearRunMapMemory(restoredState);
                ExploredNodesMemory.Clear();
                CheckpointStore.ClearCheckpointVisitedCoords(checkpoint);
            }
            else if (AmnesiaState.TimelineHidden)
            {
                // 失忆时间线结束：恢复正常显示，遭遇预告从当前层重新记录。
                AmnesiaState.SetTimelineHidden(false);
                EncounterJournalStore.ClearCurrentActRecords(checkpoint.CurrentActIndex);
            }
            if (playRecoveryAudio)
            {
                if (isAmnesiaRecovery)
                    RecoveryAudio.PlayFirstRecoveryAudio();
                else
                    RecoveryAudio.TryPlayRecovery(checkpoint);
            }
            // LoadRun reconstructs the serialized checkpoint and overwrites
            // any cards added before it. Apply the recovery-only changes only
            // after reconstruction, so Guilty is actually present in deck.
            await CheckpointStore.ApplyRecoveryState(restoredState, checkpoint);
            EncounterJournalStore.SetPreviewAct(checkpoint.CurrentActIndex);
            // LoadRun only reconstructs the run in memory. Persist the
            // recovered state immediately, otherwise a later process restart
            // loads the pre-recovery native save and loses the return.
            // 注意：preFinishedRoom 不能是火堆房间——原生 FromSerializable
            // 不支持 RestSite 类型（会抛 ArgumentOutOfRange 导致 SL 黑屏）。
            // 传 null 让载入时按最后到访坐标重建房间，完成态由火堆标记接管。
            var roomToSave = restoredState.CurrentRoom is RestSiteRoom ? null : restoredState.CurrentRoom;
            await SaveManager.Instance.SaveRun(roomToSave, true);
            ModLog.Write("Recovered run persisted to the native save.");
            // 本次运行发生过死亡回归；蕾姆事件的触发条件依赖该标记。
            // 同时登记本局唯一一次特殊标题报幕机会；失忆回归也在这里计入。
            RouteState.MarkRecoveredFromDeath(restoredState.CurrentActIndex);
            RecoveryMarker.ReleaseNativeRunPreservation();
            RecoveryMarker.Complete();
            ModLog.Write("Checkpoint restoration completed.");
        }
        catch (Exception exception)
        {
            EncounterJournalStore.SetReplayActive(false);
            RecoveryMarker.ReleaseForRetry();
            ModLog.Write($"Checkpoint restoration failed: {exception}");
        }
        finally
        {
            Volatile.Write(ref _isRestoring, 0);
        }
    }

    // 失忆回归：清空运行状态里的已走节点（保留最后一个，即当前位置——
    // 原版按“最后走过的节点”计算可通行状态，全部清空会导致地图无法前进）。
    // 失忆时间线的地图只显示当前位置与可选节点，更早的足迹不再保留。
    // 检查点文件与记忆面板的历史快照不受影响。
    private static void ClearRunMapMemory(RunState state)
    {
        try
        {
            if (AccessTools.Field(typeof(RunState), "_visitedMapCoords")?.GetValue(state) is List<MapCoord> visited &&
                visited.Count > 0)
            {
                var current = visited[^1];
                visited.Clear();
                visited.Add(current);
            }

            ModLog.Write("Amnesia recovery: run map memory cleared.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Amnesia map memory clear failed: {exception}");
        }
    }
}

// 接受奥托后不在当前设置页直接删牌，而是先保存并重新载入运行。
// LoadRun 完成后再执行删牌，这样删牌动画会出现在重新进入地图、事件或战斗页面之后。
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterAct), new[] { typeof(int), typeof(bool) })]
internal static class RunManagerEnterActPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunManager __instance, ref Task __result)
    {
        if (!__instance.IsSingleplayerOrFakeMultiplayer)
            return;

        __result = SaveCheckpointAfterEnteringActAsync(__result, __instance);
    }

    private static async Task SaveCheckpointAfterEnteringActAsync(Task enterActTask, RunManager runManager)
    {
        await enterActTask;
        if (runManager.DebugOnlyGetState() is { } state)
        {
            if (state.CurrentActIndex == 0)
            {
                CheckpointStore.CaptureCurrentAct(runManager);
                // 第一层先古选项之前的卡组即初始卡组，供失忆回归重置使用。
                AmnesiaState.CaptureInitialDeck(state);
            }
        }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
internal static class RunManagerEnterNextActPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunManager __instance, ref Task __result)
    {
        if (!__instance.IsSingleplayerOrFakeMultiplayer)
            return;

        __result = SetAncientHealthAfterEnteringAsync(__result, __instance);
    }

    private static async Task SetAncientHealthAfterEnteringAsync(Task enterNextActTask, RunManager runManager)
    {
        await enterNextActTask;
        var state = runManager.DebugOnlyGetState();
        if (state is not null)
            EncounterJournalStore.SetPreviewAct(state.CurrentActIndex);
    }
}

// 最终 Boss 战结算的“前进”按钮最终都会调用 RunManager.EnterNextAct。
// 直接拦截这个实际的推进入口，而不依赖 UI 节点的回调实现；视频结束时再放行
// 同一次原版 EnterNextAct，因此建筑师仍由游戏本身正常进入。
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded), new[] { typeof(bool) })]
internal static class RunManagerOnEndedPatch
{
    [HarmonyPrefix]
    // Prefix 阶段先暂缓原版死亡存档清理；建筑师则直接放行原版终局。
    private static void Prefix(RunManager __instance, bool isVictory)
    {
        if (ArchitectFinaleState.IsActive || SlothFinalBossTransition.IsDeathSettlementActive)
        {
            RecoveryMarker.StopRecoveryForArchitect();
            return;
        }

        if (!isVictory && __instance.IsSingleplayerOrFakeMultiplayer && CheckpointStore.TryLoad(out _))
        {
            // Native end-of-run processing deletes the active save and writes
            // a loss before the game-over screen is shown. Hold those writes
            // until the recovery state has replaced the native save.
            RecoveryMarker.PreserveNativeRun();
            ModLog.Write("Holding native death settlement for recovery.");
        }
    }

    [HarmonyPostfix]
    // Postfix 能拿到死亡时的 SerializableRun；这里只合并牌组以外允许跨线
    // 保留的内容，牌组本身始终来自此前增量更新过的检查点。
    private static void Postfix(RunManager __instance, bool isVictory, SerializableRun __result)
    {
        if (isVictory || ArchitectFinaleState.IsActive || SlothFinalBossTransition.IsDeathSettlementActive ||
            !__instance.IsSingleplayerOrFakeMultiplayer)
            return;

        // OnEnded runs only after native revive effects have been exhausted.
        // No game save is awaited here, so the standard death flow can finish.
        if (CheckpointStore.TryPrepareRecovery(__result))
        {
            if (__instance.DebugOnlyGetState() is { } deathState)
            {
                ExploredNodesMemory.Record(deathState);
                // 暂停失忆事件：第三层死亡当前走普通死亡回归。保留
                // AmnesiaState 实现，后续完成剧情后再重新启用此入口。
                // AmnesiaState.TryMarkAct3Death(deathState);
            }
            RecoveryMarker.Mark();
            ModLog.Write("True death marked for checkpoint recovery.");
        }
        else
        {
            RecoveryMarker.ReleaseNativeRunPreservation();
        }
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentRun))]
internal static class PreserveNativeRunSavePatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RecoveryMarker.ShouldPreserveNativeRun;
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRunHistory))]
internal static class PreserveNativeRunHistoryPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RecoveryMarker.ShouldPreserveNativeRun;
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.UpdateProgressWithRunData))]
internal static class PreserveNativeRunProgressPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RecoveryMarker.ShouldPreserveNativeRun;
}

// 死亡结算的存档批次里，原版还会直接调用静态的
// RunHistoryUtilities.CreateRunHistoryEntry 把死亡世界线写进跨局永久历史
// （前三个 SaveManager 补丁拦不到它）。这里一并拦下：永久历史只保留最终
// 通关的那一条记录——通关时局内历史正是各存档点拼起来的获胜时间线。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Runs.RunHistoryUtilities),
    nameof(MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry))]
internal static class PreserveNativeRunHistoryEntryPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RecoveryMarker.ShouldPreserveNativeRun;
}

[HarmonyPatch(typeof(NGameOverScreen), "OpenSummaryScreen")]
internal static class GameOverContinuePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!RecoveryMarker.TryBegin() || !CheckpointStore.TryLoad(out var checkpoint))
            return true;

        if (!RecoveryFlow.TryStart())
            return false;

        _ = RecoveryFlow.RestoreCheckpointAsync(checkpoint, playRecoveryAudio: true);
        return false;
    }
}

// 当牌组中至少有 3 张愧疚时，拦截“放弃”的最终确认，播放特殊视频。
// 视频结束后显示两个分支按钮；本事件每个运行只允许做出一次选择。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.NGame), "LoadMainMenu")]
internal static class MainMenuRecoveryPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        if (!RecoveryMarker.TryBegin() || !CheckpointStore.TryLoad(out var checkpoint))
            return;

        if (!RecoveryFlow.TryStart())
            return;

        __result = RestoreAfterMainMenuLoadsAsync(__result, checkpoint);
    }

    private static async Task RestoreAfterMainMenuLoadsAsync(Task mainMenuTask, SerializableRun checkpoint)
    {
        await mainMenuTask;
        await RecoveryFlow.RestoreCheckpointAsync(checkpoint, playRecoveryAudio: false);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer))]
internal static class NewRunPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        ArchitectFinaleState.Reset();
        RouteState.ResetForNewRun();
        MapNodeVisibilityFilter.ResetOpenEye();
        PrideFinalBossOpening.ResetForNewRun();
        PrideFinalBossTransition.ResetForNewRun();
        SlothFinalBossTransition.ResetForNewRun();
        AbandonRunVideo.ResetForNewRun();
        RewardSnapshotStore.Clear();
        ExploredNodesMemory.Clear();
        TruePlaytimeTracker.Reset();
        OttoSettlementCheckpoint.Clear();
        AmnesiaState.ResetForNewRun();
        RemEventState.ResetForNewRun();
        ProxyModeState.Reset();
        EchidnaVisitState.ResetForNewRun();
        GreedIfState.ResetForNewRun();
        GreedFinaleState.Reset();
        RecoveryMarker.Clear();
        // Encounter journal data belongs to one run. Never let a prior run's
        // coordinates affect the first rooms of a new run.
        EncounterJournalStore.Clear();
    }

    [HarmonyPostfix]
    private static void Postfix(RunManager __instance)
    {
        CheckpointStore.SetInitialHealth(__instance);
        // 新开局不一定经过 NGame.LoadRun；延后一帧主动挂上头像入口，
        // 让记忆面板从开局就可用，而不是等失忆回归后才出现。
        _ = AttachMemoryButtonAfterNewRunAsync();
    }

    private static async Task AttachMemoryButtonAfterNewRunAsync()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            AmnesiaMemoryPanel.EnsureButton();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Memory button new-run hook failed: {exception.Message}");
        }
    }
}
