// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

/// <summary>
/// 只在特殊事件中生成的卡牌。它直接使用华丽收场的牌面资源与同一套终结特效，
/// 但独立定义费用、可用条件、文字和强制击杀效果。
/// 保留/消耗词条由 CanonicalKeywords 声明、原生描述管线自动渲染
/// （保留在最前、消耗在最后，带换行）；“击晕”按吹哨的写法用 [gold] 标记。
/// </summary>
public sealed class OttoCard : CardModel
{
    // 稀有度用 Common 是为了卡面横幅取普通牌的白色（Rare 会渲染成金色，
    // 与这张牌只在事件中临时出现的气质不符）。奥托不会被任何生成来源产出
    // （CanBeGeneratedInCombat / CanBeGeneratedByModifiers 已强制为 false），
    // 因此降低稀有度不会让它进入卡牌奖励或商店池。
    public OttoCard() : base(0, CardType.Skill, CardRarity.Common, TargetType.AllEnemies, false) { }

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Retain, CardKeyword.Exhaust };

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 本回合获得力量（升级后 10）：复用原版 Flex 药水的临时力量能力
        // （回合结束自动移除）。
        if (Owner?.Creature is { } owner)
            await PowerCmd.Apply<FlexPotionPower>(choiceContext, owner, IsUpgraded ? 10m : 6m, owner, null);

        // 击晕所有敌人。
        var enemies = CombatState?.Enemies.Where(enemy => enemy.IsAlive).ToList()
            ?? new List<Creature>();
        foreach (var enemy in enemies)
        {
            await CreatureCmd.TriggerAnim(enemy, "Hit", 0f);
            await CreatureCmd.Stun(enemy);
        }
    }

    public override string PortraitPath => ModelDb.Card<ThinkingAhead>().PortraitPath;
}

internal static class OttoAcceptanceState
{
    private static readonly string Path = System.IO.Path.Combine(
        ModLog.ModDirectory, "return-by-death.otto-accepted");
    private const string Content = "return-by-death-otto-accepted-v1";

    public static bool IsPending
    {
        get
        {
            try { return File.ReadAllText(Path) == Content; }
            catch { return false; }
        }
    }

    public static void Mark()
    {
        try { File.WriteAllText(Path, Content); }
        catch (Exception exception) { ModLog.Write($"Could not persist Otto acceptance state: {exception.Message}"); }
    }

    public static void Clear()
    {
        try { File.Delete(Path); } catch { }
    }
}
internal static class OttoCardLifecycle
{
    private static int _addedThisCombat;

    public static void ResetCombat()
    {
        Volatile.Write(ref _addedThisCombat, 0);
    }

    public static bool HasOttoInCombat(Player player) =>
        player.Piles
            .Where(pile => pile.IsCombatPile)
            .SelectMany(pile => pile.Cards)
            .Any(card => card is OttoCard);

    public static bool ShouldAddToFirstHand(ICombatState combatState, Player player)
    {
        if (!OttoAcceptanceState.IsPending ||
            player.PlayerCombatState is null ||
            player.PlayerCombatState.TurnNumber != 1 ||
            HasOttoInCombat(player))
            return false;

        return Interlocked.CompareExchange(ref _addedThisCombat, 1, 0) == 0;
    }

    // 奥托的契约遗物的注入条件：与接受奥托共用每场战斗一次的计数，
    // 两条路径同时存在时合计只会加入一张奥托。
    public static bool ShouldAddForRelic(Player player)
    {
        if (player.PlayerCombatState is null ||
            player.PlayerCombatState.TurnNumber != 1 ||
            HasOttoInCombat(player))
            return false;

        return Interlocked.CompareExchange(ref _addedThisCombat, 1, 0) == 0;
    }

    public static void MarkAccepted()
    {
        OttoAcceptanceState.Mark();
        IfAchievements.Unlock("otto_accept");
    }

    public static async Task AddToFirstHandAsync(Player player)
    {
        try
        {
            var combatState = player.Creature.CombatState
                ?? throw new InvalidOperationException("CombatState is unavailable for Otto.");
            var cards = new List<CardModel>
            {
                combatState.CreateCard<OttoCard>(player)
            };
            await CardPileCmd.AddGeneratedCardsToCombat(cards, PileType.Hand, player);
            ModLog.Write("Added temporary Otto to the first combat hand via the Radiant Pearl path.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _addedThisCombat, 0);
            ModLog.Write($"Adding Otto to the first combat hand failed: {exception}");
        }
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Description), MethodType.Getter)]
internal static class OttoDescriptionPatch
{
    // 词条走本 mod 注入的 "cards" 表：{IfUpgraded:show:10|6} 由原生管线
    // 处理升级差异；“保留/消耗”词条块由 CanonicalKeywords 自动渲染。
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (__instance is OttoCard)
            __result = new LocString("cards", "RBD_OTTO.description");
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.TitleLocString), MethodType.Getter)]
internal static class OttoTitleLocStringPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (__instance is OttoCard)
            __result = ModelDb.Card<ThinkingAhead>().TitleLocString;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Title), MethodType.Getter)]
internal static class OttoTitlePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is OttoCard)
            __result = __instance.IsUpgraded ? "奥托+" : "奥托";
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.PortraitPath), MethodType.Getter)]
internal static class OttoPortraitPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is OttoCard)
            __result = ModelDb.Card<ThinkingAhead>().PortraitPath;
    }
}

[HarmonyPatch(typeof(CardModel), "Pool", MethodType.Getter)]
internal static class OttoPoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not OttoCard)
            return true;

        __result = ModelDb.Card<MasterOfStrategy>().Pool;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "VisualCardPool", MethodType.Getter)]
internal static class OttoVisualPoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not OttoCard)
            return true;

        __result = ModelDb.Card<MasterOfStrategy>().VisualCardPool;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedInCombat", MethodType.Getter)]
internal static class OttoCombatGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is OttoCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedByModifiers", MethodType.Getter)]
internal static class OttoModifierGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is OttoCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "ShouldShowInCardLibrary", MethodType.Getter)]
internal static class OttoLibraryVisibilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is OttoCard)
            __result = false;
    }
}


/// <summary>
/// 只在特殊事件中生成的卡牌。它直接使用华丽收场的牌面资源与同一套终结特效，
/// 但独立定义费用、可用条件、文字和强制击杀效果。
/// </summary>
