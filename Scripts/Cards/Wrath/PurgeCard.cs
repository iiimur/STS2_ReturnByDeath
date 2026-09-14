// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace ReturnByDeath;

/// <summary>
/// 愤怒 IF 线的新增无色稀有攻击牌「肃清」。贴图套用原版「计算下注」，
/// 1 费造成 99 点伤害，升级后费用降为 0、名称变为「肃清+」。
/// 与奥托/莱茵哈鲁特一致：这张牌不由任何生成来源产出（战斗生成、修饰符
/// 生成与卡牌图鉴都关闭），只能由剧情/事件授予，因此不会污染任何奖励池。
/// </summary>
public sealed class PurgeCard : CardModel
{
    public PurgeCard() : base(1, CardType.Attack, CardRarity.Rare, TargetType.AnyEnemy, false) { }

    // 原生“永恒。”词条：自动渲染关键字文本，并阻止这张牌从牌组中
    // 被移除或变化；升级前后都保留。
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Eternal };

    // {Damage:diff()} 占位符由原生描述管线解析，数值随预览/升级高亮。
    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new DynamicVar[] { new DamageVar(99m, ValueProp.Move) };

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target, "cardPlay.Target");
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    // 升级只降费用：1 → 0。名称末尾的「+」由原生 Title 管线按 IsUpgraded 自动追加。
    protected override void OnUpgrade() => EnergyCost.UpgradeBy(-1);
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.PortraitPath), MethodType.Getter)]
internal static class PurgePortraitPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is PurgeCard)
            __result = ModelDb.Card<CalculatedGamble>().PortraitPath;
    }
}

// 「无色」定位：卡框与能量图标都取自无色卡池（FrameMaterial / EnergyIconPath）。
// 这张牌不在任何卡池的卡表里，因此必须自己解析 Pool，否则原版会抛
// InvalidProgramException（Card is not in any card pool）。
[HarmonyPatch(typeof(CardModel), "Pool", MethodType.Getter)]
internal static class PurgePoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not PurgeCard)
            return true;

        __result = ModelDb.CardPool<ColorlessCardPool>();
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "VisualCardPool", MethodType.Getter)]
internal static class PurgeVisualPoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not PurgeCard)
            return true;

        __result = ModelDb.CardPool<ColorlessCardPool>();
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedInCombat", MethodType.Getter)]
internal static class PurgeCombatGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is PurgeCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedByModifiers", MethodType.Getter)]
internal static class PurgeModifierGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is PurgeCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "ShouldShowInCardLibrary", MethodType.Getter)]
internal static class PurgeLibraryVisibilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is PurgeCard)
            __result = false;
    }
}
