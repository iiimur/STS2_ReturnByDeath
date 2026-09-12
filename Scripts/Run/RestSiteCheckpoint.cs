// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 火堆“休息”改名为“休息并存档”：选择休息、回血完成后立即落一次检查点，
// 回归时回到“休息完成、只剩前进按钮”的状态。RestSite 房间不在原版
// AbstractRoom.FromSerializable 的支持列表里（会抛异常），无法像先古事件那样
// 带 preFinishedRoom 重建，因此检查点不带房间快照，回归后重新进入火堆房间，
// 由下面的补丁把 UI 直接置为完成态。
internal static class RestSiteRecovery
{
    private static int _pending;

    public static void MarkPending() => Volatile.Write(ref _pending, 1);

    public static bool ConsumePending() => Interlocked.Exchange(ref _pending, 0) != 0;
}

// 休息（HEAL）完成后，当前地图点的历史记录会带上 "HEAL"（原版自己维护、
// 随存档持久化）。以它为条件在休息结算后捕获检查点；锻造等其他选项不触发。
[HarmonyPatch(typeof(RestSiteSynchronizer), "ChooseOption", new[] { typeof(Player), typeof(int) })]
internal static class RestSiteHealCheckpointPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player, ref Task<bool> __result)
    {
        var runManager = RunManager.Instance;
        if (runManager is null || !runManager.IsSingleplayerOrFakeMultiplayer)
            return;

        __result = CaptureAfterRestAsync(__result, player, runManager);
    }

    private static async Task<bool> CaptureAfterRestAsync(
        Task<bool> selectTask, Player player, RunManager runManager)
    {
        var success = await selectTask;
        if (!success)
            return success;

        var state = runManager.DebugOnlyGetState();
        var rested = state?.CurrentMapPointHistoryEntry?
            .GetEntry(player.NetId).RestSiteChoices.Contains("HEAL") == true;
        if (!rested)
            return success;

        CheckpointStore.CaptureCurrentAct(runManager);
        ModLog.Write("Rest-site checkpoint captured after healing.");
        return success;
    }
}

// 回归落点若是已休息的火堆：跳过选项按钮的重建，直接显示前进按钮。
// 标志在 LoadRun 之前由 RestoreCheckpointAsync 设置，此时房间节点尚未创建。
[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
internal static class RestSiteRecoverySkipOptionsPatch
{
    [HarmonyPrefix]
    private static bool Prefix(out bool __state)
    {
        __state = RestSiteRecovery.ConsumePending();
        // SL 载入回归落点火堆存档时同样跳过：此时原版选项重建会在存档
        // 状态不完整的情况下抛异常导致载入黑屏（恢复落地时也是跳过的）。
        if (!__state && RouteState.IsAtRestedFireCheckpoint)
            __state = true;
        return !__state;
    }

    [HarmonyPostfix]
    private static void Postfix(NRestSiteRoom __instance, bool __state)
    {
        if (!__state)
            return;

        ModLog.Write("Recovered into a rested rest site; options skipped, only the proceed button is shown.");
        try
        {
            AccessTools.Method(typeof(NRestSiteRoom), "ShowProceedButton")?.Invoke(__instance, null);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not show the proceed button on a recovered rest site: {exception}");
        }
    }
}

// 火堆文案：直接改写原版 LocString 的格式化结果，保留回血数字的原生动态变量。
internal static class RestSiteText
{
    private const string TitleText = "休息并存档";

    public static string Rewrite(LocString locString, string text)
    {
        if (!string.Equals(locString.LocTable, "rest_site_ui", StringComparison.Ordinal))
            return text;

        if (string.Equals(locString.LocEntryKey, "OPTION_HEAL.name", StringComparison.Ordinal))
            return TitleText;

        if (string.Equals(locString.LocEntryKey, "OPTION_HEAL.description", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(text))
        {
            // 在第一句句号前插入“并存档”，保留动态回血数字；防止重复插入。
            var sentenceEnd = text.IndexOf('。');
            if (sentenceEnd >= 0 && !text.Contains("并存档", StringComparison.Ordinal))
                text = text.Insert(sentenceEnd, "并存档");
        }

        return text;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
internal static class RestSiteHealFormattedTextPatch
{
    [HarmonyPostfix]
    private static void Postfix(LocString __instance, ref string __result) =>
        __result = RestSiteText.Rewrite(__instance, __result);
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
internal static class RestSiteHealRawTextPatch
{
    [HarmonyPostfix]
    private static void Postfix(LocString __instance, ref string __result) =>
        __result = RestSiteText.Rewrite(__instance, __result);
}
