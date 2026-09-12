// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

/// <summary>
/// 只在特殊事件中生成的卡牌。它直接使用华丽收场的牌面资源与同一套终结特效，
/// 但独立定义费用、可用条件、文字和强制击杀效果。
/// </summary>
public sealed class OttoCard : CardModel
{
    public OttoCard() : base(0, CardType.Skill, CardRarity.Rare, TargetType.AnyEnemy, false) { }

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        IsUpgraded
            ? new[] { CardKeyword.Retain, CardKeyword.Exhaust }
            : new[] { CardKeyword.Exhaust };

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Target is { } target && target.IsAlive)
        {
            await CreatureCmd.TriggerAnim(target, "Hit", 0f);
            await CreatureCmd.Stun(target);
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

    public static void MarkAccepted() => OttoAcceptanceState.Mark();

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
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (__instance is OttoCard)
            __result = ModelDb.Card<ThinkingAhead>().Description;
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

[HarmonyPatch]
internal static class OttoDescriptionRenderPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(CardModel)).Where(method =>
            method.Name is "GetDescriptionForPile" or "GetDescriptionForUpgradePreview");

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is not OttoCard)
            return;

        __result = __instance.IsUpgraded
            ? "保留。击晕该敌人。消耗。"
            : "击晕该敌人。消耗。";
    }
}


/// <summary>
/// 只在特殊事件中生成的卡牌。它直接使用华丽收场的牌面资源与同一套终结特效，
/// 但独立定义费用、可用条件、文字和强制击杀效果。
/// </summary>
