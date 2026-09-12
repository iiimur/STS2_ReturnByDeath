// 代理战斗的致死预算与地图进入前确认窗。

namespace ReturnByDeath;

internal static class ProxyDeathWarning
{
    private const string WarningText = "继续代理会被视为本局失败。";
    private static TaskCompletionSource<bool>? _popupCompletion;
    private static int _active;
    private static int _bypassNextTravel;

    public static bool IsPopupActive => Volatile.Read(ref _active) != 0;

    public static bool TryIntercept(NMapScreen screen, MapCoord coord, out Task result)
    {
        result = Task.CompletedTask;
        if (Volatile.Read(ref _bypassNextTravel) != 0)
            return false;

        // 模态窗已经打开时吞掉重复点击，保留正在等待的第一次选择。
        if (IsPopupActive)
            return true;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var player = state?.Players.FirstOrDefault();
        if (!ProxyModeState.Enabled || SlothRouteRules.IsActive ||
            state is null || player is null ||
            !EncounterJournalStore.TryGetUpcomingProxyHpLoss(state, coord, out var hpLoss) ||
            hpLoss <= 0 || hpLoss < player.Creature.CurrentHp)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return true;

        result = PromptThenMaybeTravelAsync(screen, coord, hpLoss, player.Creature.CurrentHp);
        return true;
    }

    public static bool TryConsumePopupChoice(bool yes)
    {
        var completion = _popupCompletion;
        if (!IsPopupActive || completion is null)
            return false;

        _popupCompletion = null;
        completion.TrySetResult(yes);
        return true;
    }

    private static async Task PromptThenMaybeTravelAsync(
        NMapScreen screen,
        MapCoord coord,
        int hpLoss,
        int currentHp)
    {
        try
        {
            var proceed = await ShowPopupAsync();
            if (!proceed)
            {
                // “不了”：不进入战斗。原版投票同步器在入队移动动作前就已把
                // 接受投票的来源坐标前进到目标（MapSelectionSynchronizer.
                // MoveToMapCoord），不还原的话后续所有投票都会因来源不匹配
                // 被拒绝，玩家将无法再前进。
                RestoreMapVoteSynchronizer();
                ModLog.Write($"Proxy lethal warning cancelled travel: coord={coord}, " +
                    $"hp={currentHp}, projected loss={hpLoss}.");
                return;
            }

            ModLog.Write($"Proxy lethal warning confirmed: coord={coord}, " +
                $"hp={currentHp}, projected loss={hpLoss}.");
            Volatile.Write(ref _bypassNextTravel, 1);
            try
            {
                if (GodotObject.IsInstanceValid(screen))
                    await screen.TravelToMapCoord(coord);
            }
            finally
            {
                Volatile.Write(ref _bypassNextTravel, 0);
            }
        }
        catch (Exception exception)
        {
            // 弹窗本身失效时不把地图卡死，回退到玩家原本选择的代理行动。
            ModLog.Write($"Proxy lethal warning failed; continuing travel: {exception}");
            Volatile.Write(ref _bypassNextTravel, 1);
            try
            {
                if (GodotObject.IsInstanceValid(screen))
                    await screen.TravelToMapCoord(coord);
            }
            finally
            {
                Volatile.Write(ref _bypassNextTravel, 0);
            }
        }
        finally
        {
            _popupCompletion = null;
            Volatile.Write(ref _active, 0);
        }
    }

    // 把投票同步器恢复到“仍在当前坐标接受投票”的状态：按当前层与坐标调用
    // 原版 OnLocationChanged（私有），它会重置来源坐标并清空未完成的投票。
    private static void RestoreMapVoteSynchronizer()
    {
        var synchronizer = RunManager.Instance?.MapSelectionSynchronizer;
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (synchronizer is null || state is null || !state.CurrentMapCoord.HasValue)
            return;

        var location = new MegaCrit.Sts2.Core.Runs.MapLocation(
            state.CurrentMapCoord.Value, state.CurrentActIndex);
        AccessTools.Method(synchronizer.GetType(), "OnLocationChanged")?
            .Invoke(synchronizer, new object[] { location });
    }

    private static async Task<bool> ShowPopupAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _popupCompletion = completion;

        var popup = NAbandonRunConfirmPopup.Create(null)
            ?? throw new InvalidOperationException("Abandon confirm popup is unavailable.");
        NModalContainer.Instance!.Add(popup);

        // AddChildSafely 可能延迟 _Ready；等按钮完成原版初始化后再替换正文，
        // 否则迟到的 _Ready 会用原版文案把警告文字覆盖回去。最多等 1.2 秒，
        // 超时也照样尝试替换（蕾姆事件同款弹窗的成熟等待模式）。
        for (var i = 0; i < 60; i++)
        {
            if (AccessTools.Field(typeof(NPopupYesNoButton), "_label")?
                .GetValue(popup.GetNode<NVerticalPopup>("VerticalPopup").YesButton) is not null)
                break;
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                var timer = tree.CreateTimer(0.02, true, false, true);
                await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitSignal(
                    timer, SceneTreeTimer.SignalName.Timeout, tree.Root);
            }
        }

        var description = popup.GetNode<MegaRichTextLabel>("VerticalPopup/Description");
        description.BbcodeEnabled = false;
        description.SetTextAutoSize(WarningText);
        ModLog.Write("Proxy lethal warning popup opened.");
        return await completion.Task;
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord), new[] { typeof(MapCoord) })]
internal static class ProxyDeathWarningTravelPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMapScreen __instance, MapCoord coord, ref Task __result)
    {
        if (!ProxyDeathWarning.TryIntercept(__instance, coord, out var warningTask))
            return true;

        __result = warningTask;
        return false;
    }
}

[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnYesButtonPressed")]
internal static class ProxyDeathWarningYesPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => !ProxyDeathWarning.TryConsumePopupChoice(yes: true);
}

[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnNoButtonPressed")]
internal static class ProxyDeathWarningNoPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => !ProxyDeathWarning.TryConsumePopupChoice(yes: false);
}
