// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class AncientHealContext
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) != 0;

    public static void Begin() => Volatile.Write(ref _active, 1);

    public static void End() => Volatile.Write(ref _active, 0);
}

[HarmonyPatch(typeof(AncientEventModel), "BeforeEventStarted", new[] { typeof(bool) })]
internal static class AncientBeforeEventStartedPatch
{
    [HarmonyPrefix]
    private static void Prefix() => AncientHealContext.Begin();

    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        if (__result is null)
        {
            AncientHealContext.End();
            return;
        }

        _ = ClearContextWhenFinishedAsync(__result);
    }

    private static async Task ClearContextWhenFinishedAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            AncientHealContext.End();
        }
    }
}

// 本地游戏版本中的 Heal 返回 Task，而不是“实际回血数值”。
// 因此通过 ref decimal amount 修改原版 Heal 的输入，让原版逻辑完成最终回血。
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal), new[] { typeof(Creature), typeof(decimal), typeof(bool) })]
internal static class AncientHealAmountPatch
{
    [HarmonyPrefix]
    private static void Prefix(Creature creature, ref decimal amount)
    {
        if (!AncientHealContext.IsActive || !creature.IsPlayer)
            return;

        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null)
            return;

        if (state.CurrentActIndex == 0)
        {
            // 第一层先古的回血完全保留给原版流程，避免在选项出现前
            // 就把生命值固定为 4；选项完成后由 AncientEventDonePatch 设置为 4。
            return;
        }

        // 二、三层先古事件不恢复血量，也不通过负数回血扣血。
        amount = 0m;
        ModLog.Write($"Ancient Heal suppressed for act {state.CurrentActIndex}; amount set to 0.");
    }
}

[HarmonyPatch(typeof(AncientEventModel), "Done")]
internal static class AncientEventDonePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        var runManager = RunManager.Instance;
        if (runManager.IsSingleplayerOrFakeMultiplayer)
        {
            // 第一层在选项完成后固定到 4；二、三层保留原版流程结束后的
            // 实际血量，不再额外恢复或扣血。随后以此血量创建检查点。
            CheckpointStore.SetAncientHealthForCompletedAct(runManager);
            // The Ancient choice is the reliable boundary between acts. Clear
            // the old act's forecast together with the previous act's health.
            EncounterJournalStore.ClearPreview();
            CheckpointStore.CaptureAncientCompletion(runManager);
            // CaptureAncientCompletion writes the mod checkpoint, but the
            // native run save may still contain the pre-Ancient HP. Persist
            // the modified 4/86 state as well, so a normal save-and-quit/SL
            // cannot restore the old health value.
            _ = SaveAncientCompletionAsync(runManager);
        }
    }

    private static async Task SaveAncientCompletionAsync(RunManager runManager)
    {
        try
        {
            var state = runManager.DebugOnlyGetState();
            if (state is null)
                return;

            await SaveManager.Instance.SaveRun(state.CurrentRoom, true);
            ModLog.Write($"Ancient completion persisted to native save: hp={state.Players.FirstOrDefault()?.Creature.CurrentHp}/{state.Players.FirstOrDefault()?.Creature.MaxHp}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Ancient completion native save failed: {exception}");
        }
    }
}

// 问号事件中的部分选项会直接令玩家死亡。原版会在 EventOption 上用
// WillKillPlayer 委托统一表达这件事：ThatDoesDamage 会依据当前生命值生成
// 判定，而 ThatWillKillPlayerIf 用于“真相石板”“试炼”等条件性必死选项。
// 因而无需维护易随版本变化而失效的事件名称白名单。
