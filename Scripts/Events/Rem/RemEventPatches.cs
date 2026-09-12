// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 暂停菜单（ESC）的“放弃”按钮：条件满足时接管点击，打开/刷新时同步按钮外观。
[HarmonyPatch(typeof(NPauseMenu), "OnGiveUpButtonPressed")]
internal static class RemPauseMenuGiveUpPatch
{
    [HarmonyPrefix]
    private static bool Prefix() =>
        !SlothRouteRules.IsActive && !RemEventState.TryHandleGiveUpPressed();
}

// 蕾姆流程弹窗期间的确认窗点击：结果交回蕾姆状态机，拦截原版的放弃/关闭行为。
// 弹窗自身由按钮内置的 Close（NModalContainer.Clear）正常关闭。
[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnYesButtonPressed")]
internal static class RemPopupYesPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RemEventState.TryConsumePopupChoice(yes: true);
}

[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnNoButtonPressed")]
internal static class RemPopupNoPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !RemEventState.TryConsumePopupChoice(yes: false);
}

// 设置屏幕内的放弃按钮（部分入口使用）同样接管。
[HarmonyPatch(typeof(NAbandonRunButton), "OnRelease")]
internal static class RemAbandonRunButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NAbandonRunButton __instance) =>
        !SlothRouteRules.IsActive && !RemEventState.TryHandleGiveUpPressed();
}

// 怠惰线中不仅拦截放弃操作，也将暂停菜单内的按钮设为禁用态。
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
internal static class RemMapAdvancePatch
{
    [HarmonyPostfix]
    private static void Postfix() => RemEventState.OnMapAdvanced();
}

// 蕾姆事件最终选择“不了”后，第一层 Boss 胜利额外获得原版事件专属遗物
// 遗忘之魂。挂在房间奖励入口并等待原版任务完成，避免抢在 Boss 奖励生成前
// 修改遗物栏；RelicCmd.Obtain 负责原生入手动画、遗物登记和存档状态。
[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
internal static class RemForgottenSoulBossRewardPatch
{
    [HarmonyPostfix]
    private static void Postfix(CombatRoom __instance, ref Task __result)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state?.CurrentActIndex != 0 || __instance.RoomType != RoomType.Boss)
            return;

        var nativeSettlement = __result;
        __result = GrantAfterBossSettlementAsync(nativeSettlement);
    }

    private static async Task GrantAfterBossSettlementAsync(Task nativeSettlement)
    {
        await nativeSettlement;
        if (!RemEventState.TryBeginForgottenSoulGrant())
            return;

        var granted = false;
        try
        {
            // OfferRoomEndRewards 会异步建立奖励 UI；让出一帧后再播放原生遗物
            // 入手流程，保证不会与 Boss 奖励窗口的首次挂载重叠。
            await Task.Yield();
            var player = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault()
                ?? throw new InvalidOperationException("No player was available for the Forgotten Soul reward.");
            await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.ForgottenSoul>(player);
            granted = true;
            ModLog.Write("Rem reward branch: granted Forgotten Soul after the act 1 Boss victory.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem Forgotten Soul grant failed: {exception}");
        }
        finally
        {
            RemEventState.FinishForgottenSoulGrant(granted);
        }
    }
}
