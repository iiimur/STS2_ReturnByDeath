// 蕾姆“不了”分支所选稀有牌的费用规则：数字费用永久为 0，X 费保持原样。

namespace ReturnByDeath;

internal static class RemRewardCardCost
{
    private static readonly FieldInfo? CardField =
        AccessTools.Field(typeof(CardEnergyCost), "_card");

    public static CardModel? GetCard(CardEnergyCost energyCost) =>
        CardField?.GetValue(energyCost) as CardModel;
}

// 所有费用显示、支付与结算最终都会经过 GetWithModifiers。放在这里覆写，
// 可让保存退出或死亡回归后重新构造的奖励牌继续保持数字费用为 0；
// X 费牌直接跳过，不修改其显示、支付或 X 值结算。
[HarmonyPatch(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers))]
internal static class RemRewardCardEnergyCostPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardEnergyCost __instance, ref int __result)
    {
        if (!__instance.CostsX &&
            RemEventState.IsRewardCostCard(RemRewardCardCost.GetCard(__instance)))
            __result = 0;
    }
}
