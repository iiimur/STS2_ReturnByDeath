// 傲慢之心：傲慢线的新机制遗物（贴图套用黑石护符 darkstone_periapt）。
// 持有后，死亡诅咒（愧疚/受伤）保留原有的卡牌描述与贴图不变，但可以按
// 0 费打出；打出时从三张升级后的随机牌中选一张加入手牌，该牌本回合免费。
// 随机卡池只有持有者自己角色的全部卡牌（含该角色的专属先古卡），不含
// 联机卡、其他角色的卡牌与公共先古卡。
//
// 实现要点：愧疚/受伤的“不可打出”是规范关键字（CardModel.RemoveKeyword
// 只能移除局部关键字，无效），因此可打出判定与费用解析各挂一个 Harmony
// 后缀补丁；打出效果挂在 CardModel.OnPlay 后缀（两张诅咒卡都没有自己的
// OnPlay 重载，基类方法对它们生效）。取样不走原生
// CardFactory.GetDistinctForCombat——它的 FilterForCombat 会把先古卡
// 排除掉，而本遗物需要保留角色池里的先古卡。

using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Extensions;

namespace ReturnByDeath;

public sealed class HeartOfPride : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "darkstone_periapt";

    // 诅咒卡在持有者手里是否可按 0 费打出。
    public static bool IsCursePlayable(CardModel? card) =>
        card is (Guilty or Injury) &&
        card.Owner?.Relics.Any(relic => relic is HeartOfPride) == true;

    // 打出死亡诅咒的效果：三张升级后的随机牌选一加入手牌，本回合免费。
    // 卡池只有持有者自己角色的全部卡牌（含该角色的专属先古卡），并按原生
    // 规则过滤联机卡（GetUnlockedCards 的 CardMultiplayerConstraint 参数）；
    // 其他角色的先古卡与公共先古卡都不出现。
    public static async Task PlayCurseEffectAsync(CardModel card, PlayerChoiceContext choiceContext)
    {
        var owner = card.Owner;
        var combatState = card.CombatState ?? owner?.Creature.CombatState;
        if (owner is null || combatState is null)
            return;

        try
        {
            var candidates = owner.Character.CardPool
                .GetUnlockedCards(owner.UnlockState, owner.RunState.CardMultiplayerConstraint)
                .Distinct()
                .ToList();
            var choices = candidates
                .TakeRandom(3, owner.RunState.Rng.CombatCardGeneration)
                .Select(prototype => combatState.CreateCard(prototype, owner))
                .ToList();
            foreach (var choice in choices)
            {
                try { CardCmd.Upgrade(choice); }
                catch (Exception exception) { ModLog.Write($"Pride curse choice upgrade failed: {exception.Message}"); }
            }

            if (choices.Count == 0)
                return;

            var selected = await CardSelectCmd.FromChooseACardScreen(choiceContext, choices, owner, canSkip: false);
            if (selected is null)
                return;

            selected.SetToFreeThisTurn();
            await CardPileCmd.AddGeneratedCardToCombat(selected, PileType.Hand, owner);
            ModLog.Write("Heart of Pride: death curse played; an upgraded card joined the hand (free this turn).");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Heart of Pride curse play failed: {exception}");
        }
    }
}

// 可打出判定：清除“不可打出”关键字给出的阻断位（UnplayableReason 是
// [Flags] 枚举，其余阻断原因保持原样）。
[HarmonyPatch]
internal static class PrideCurseCanPlayPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(CardModel), "CanPlay", new[]
        {
            typeof(UnplayableReason).MakeByRefType(),
            typeof(AbstractModel).MakeByRefType(),
        }) ?? throw new MissingMethodException(typeof(CardModel).FullName, "CanPlay");

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref UnplayableReason reason, ref AbstractModel? preventer, ref bool __result)
    {
        if (__result || !HeartOfPride.IsCursePlayable(__instance))
            return;

        reason &= ~UnplayableReason.HasUnplayableKeyword;
        __result = reason == UnplayableReason.None;
        if (__result)
            preventer = null;
    }
}

// 费用解析：愧疚/受伤的规范费用是 -1（“不可打出”的内部表示），
// CardEnergyCost.GetWithModifiers 对负数基础费用直接原样返回；这里改为 0，
// 显示与实际扣费（GetAmountToSpend/GetResolved 均经由该方法）统一归零。
[HarmonyPatch(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers))]
internal static class PrideCurseCostPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardEnergyCost __instance, ref int __result)
    {
        var card = AccessTools.Field(typeof(CardEnergyCost), "_card")?.GetValue(__instance) as CardModel;
        if (HeartOfPride.IsCursePlayable(card) && __result < 0)
            __result = 0;
    }
}

// 打出效果：两张诅咒卡都没有 OnPlay 重载，基类 CardModel.OnPlay 对它们
// 生效；后缀在原逻辑完成后接上三选一的发现效果。
[HarmonyPatch(typeof(CardModel), "OnPlay")]
internal static class PrideCurseOnPlayPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, PlayerChoiceContext choiceContext, ref Task __result)
    {
        if (!HeartOfPride.IsCursePlayable(__instance))
            return;

        __result = PlayAfterNativeAsync(__instance, choiceContext, __result);
    }

    private static async Task PlayAfterNativeAsync(CardModel card, PlayerChoiceContext choiceContext, Task original)
    {
        await original;
        await HeartOfPride.PlayCurseEffectAsync(card, choiceContext);
    }
}
