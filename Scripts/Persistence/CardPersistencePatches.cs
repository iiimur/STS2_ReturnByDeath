// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal sealed class CheckpointCardTransformation
{
    public ulong PlayerNetId { get; init; }
    public ModelId OriginalId { get; init; } = ModelId.none;
    public int OriginalUpgradeLevel { get; init; }
}

// CardPileCmd 的前三个 Add 重载最终都会进入这个批量 + CardPile 的核心重载。
// 只观察成功进入永久 Deck 的结果，因此战斗中的生成牌、抽牌和移牌不写检查点。
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), new[]
{
    typeof(IEnumerable<CardModel>),
    typeof(CardPile),
    typeof(CardPilePosition),
    typeof(AbstractModel),
    typeof(bool),
    typeof(bool)
})]
internal static class CheckpointCardGainPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result) =>
        __result = RecordAfterAddAsync(__result);

    private static async Task<IReadOnlyList<CardPileAddResult>> RecordAfterAddAsync(
        Task<IReadOnlyList<CardPileAddResult>> addTask)
    {
        var results = await addTask;
        try
        {
            var gained = results
                .Where(result => result.success && result.targetPile == PileType.Deck && result.cardAdded is not null)
                .Select(result => result.cardAdded)
                .ToList();
            CheckpointStore.RecordCardGains(gained, "CardPileCmd.Add");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Checkpoint card-gain observer skipped an incompatible card: {exception.Message}");
        }
        return results;
    }
}

// UpgradeInternal 是所有升级命令最终修改等级的位置。只有卡牌仍位于玩家永久
// 牌组中时才写入，战斗内临时升级和升级预览不会污染检查点。
[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
internal static class CheckpointCardUpgradePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __instance, out int __state) =>
        __state = __instance.CurrentUpgradeLevel;

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, int __state)
    {
        try
        {
            CheckpointStore.RecordCardUpgrade(__instance, __state);
        }
        catch (Exception exception)
        {
            // 这是全局 CardModel 方法，兼容性优先：记录失败绝不能让原版或
            // CombatSolver 一类使用临时卡的 mod 一起失败。
            ModLog.Write($"Checkpoint card-upgrade observer skipped an incompatible card: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Transform), new[]
{
    typeof(IEnumerable<CardTransformation>),
    typeof(MegaCrit.Sts2.Core.Random.Rng),
    typeof(CardPreviewStyle)
})]
internal static class CheckpointCardTransformationPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        ref IEnumerable<CardTransformation> transformations,
        out List<CheckpointCardTransformation> __state)
    {
        __state = new List<CheckpointCardTransformation>();
        if (transformations is null)
            return;

        var list = transformations.ToList();
        transformations = list;
        foreach (var transformation in list)
        {
            if (transformation.IsInCombat ||
                !CheckpointStore.TryGetPermanentDeckOwner(transformation.Original, out var owner))
                continue;

            __state.Add(new CheckpointCardTransformation
            {
                PlayerNetId = owner.NetId,
                OriginalId = transformation.Original.Id,
                OriginalUpgradeLevel = transformation.Original.CurrentUpgradeLevel
            });
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        List<CheckpointCardTransformation> __state,
        ref Task<IEnumerable<CardPileAddResult>> __result) =>
        __result = RecordAfterTransformAsync(__result, __state);

    private static async Task<IEnumerable<CardPileAddResult>> RecordAfterTransformAsync(
        Task<IEnumerable<CardPileAddResult>> transformTask,
        IReadOnlyList<CheckpointCardTransformation> transformations)
    {
        var results = (await transformTask).ToList();
        try
        {
            var successfulDeckAdds = results.Count(result =>
                result.success && result.targetPile == PileType.Deck && result.cardAdded is not null);
            CheckpointStore.RecordCardTransformations(transformations, successfulDeckAdds);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Checkpoint card-transformation observer skipped incompatible data: {exception.Message}");
        }
        return results;
    }
}

// 原版历史页的左右箭头仍保留用于视觉和键盘/手柄操作；在死亡记忆面板打开时，
// 把它们的点击改为切换本 mod 保存的“存档→死亡”片段，避免面板看起来只有一页。
