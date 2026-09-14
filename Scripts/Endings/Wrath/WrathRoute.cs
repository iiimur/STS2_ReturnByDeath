// 愤怒 IF 线：蕾姆事件选择“不了”取得的稀有牌被主动移出牌组时进入。

namespace ReturnByDeath;

internal static class WrathRouteTrigger
{
    public static async Task EnterAfterRemovalAsync(Task nativeRemoval)
    {
        await nativeRemoval;
        if (!RemEventState.CompleteRewardCardRemoval())
            return;

        // 与正常结局触发完全相同的代码：线路标记 + 愤怒之心 + 两张「肃清」+
        // 结局演出（音效与羽化结局图）。
        await EnterWrathAsync(playPresentation: true);
        ModLog.Write("Wrath IF route triggered: Rem's permanently free rare card was removed from the deck.");
    }

    // 进入愤怒线的完整流程：线路标记 + 授予愤怒之心与两张「肃清」。
    // 结局演出（音效 + 羽化结局图）由 playPresentation 控制；控制台 boss wrath
    // 以 false 复用同一套代码，因此同样能拿到遗物、卡牌与怪物黑白效果。
    public static async Task EnterWrathAsync(bool playPresentation)
    {
        RouteState.EnterWrathRoute();
        await GrantWrathRewardsAsync();
        if (playPresentation)
            WrathEndingOverlay.TryShow();
    }

    // 愤怒线奖励：愤怒之心（走原生遗物入手动画并写入检查点，回归不消失）
    // 与两张「肃清」。两张牌同样写入检查点，死亡回归后仍保留。
    private static async Task GrantWrathRewardsAsync()
    {
        try
        {
            var player = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault();
            if (player is null)
            {
                ModLog.Write("Wrath rewards skipped: no player was available.");
                return;
            }

            if (!player.Relics.Any(relic => relic is HeartOfWrath))
            {
                var relic = await RelicCmd.Obtain<HeartOfWrath>(player);
                CheckpointStore.RecordEchidnaRelic(relic, player);
                ModLog.Write("Wrath IF route entered; Heart of Wrath granted and persisted.");
            }

            for (var i = 0; i < PurgeCardsGranted; i++)
                await AddPurgeCardAsync(player);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Wrath rewards grant failed: {exception}");
        }
    }

    private const int PurgeCardsGranted = 2;

    private static async Task AddPurgeCardAsync(Player player)
    {
        var card = player.RunState.CreateCard<PurgeCard>(player);
        try
        {
            var addResult = await CardPileCmd.Add(card, PileType.Deck);
            CardCmd.PreviewCardPileAdd(addResult);
            if (addResult.success)
            {
                ModLog.Write($"Wrath reward: added {card.Id} to the deck with the native animation.");
                return;
            }

            ModLog.Write($"Wrath reward: native deck add of {card.Id} reported failure; using the silent fallback.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Wrath reward deck add failed; using the silent fallback: {exception}");
        }

        player.Deck.AddInternal(card, silent: true);
        CheckpointStore.RecordCardGains(new[] { card }, "wrath route fallback");
    }
}

// 原生的带动画删牌入口有单卡与批量两个重载。只在调用开始时识别目标牌，
// 再等待原生任务完成后进入线路；这样删牌动画和原房间结算不会被抢断。
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(CardModel), typeof(bool) })]
internal static class WrathSingleDeckRemovalPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, out bool __state) =>
        __state = RemEventState.IsRewardCardBeingRemoved(card);

    [HarmonyPostfix]
    private static void Postfix(bool __state, ref Task __result)
    {
        if (__state)
            __result = WrathRouteTrigger.EnterAfterRemovalAsync(__result);
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(IReadOnlyList<CardModel>), typeof(bool) })]
internal static class WrathMultipleDeckRemovalPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards, out bool __state) =>
        __state = cards.Any(RemEventState.IsRewardCardBeingRemoved);

    [HarmonyPostfix]
    private static void Postfix(bool __state, ref Task __result)
    {
        if (__state)
            __result = WrathRouteTrigger.EnterAfterRemovalAsync(__result);
    }
}
