// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

public sealed class ReinhardCard : CardModel
{
    // 原版 Guilty 的 AfterCombatEnd 在 CombatsSeen 达到 5 时移除。
    private const int RemovalCombatThreshold = 5;
    private const int UpgradeThresholdBonus = 1;
    private const int MaximumUpgradeLevel = 999;

    public override int MaxUpgradeLevel => MaximumUpgradeLevel;

    // 稀有度决定金色稀有外框；不会进入奖励池，因为下方的生成权限补丁仍为 false。
    public ReinhardCard() : base(0, CardType.Attack, CardRarity.Rare, TargetType.AllEnemies, false) { }

    public override IEnumerable<CardKeyword> CanonicalKeywords =>
        new[] { CardKeyword.Innate };

    // 与原版 Guilty 保持相同的存档字段语义：记录这张牌已经经历过的战斗数。
    // 转换愧疚时复制该值，因此莱茵哈鲁特继承原愧疚的剩余战斗数。
    private int _combatsSeen;

    [SavedProperty]
    public int CombatsSeen
    {
        get => _combatsSeen;
        set => _combatsSeen = Math.Max(0, value);
    }

    public int UpgradeCount => CurrentUpgradeLevel;

    // 预览调用时原版已经临时应用了下一级 CurrentUpgradeLevel，不能再次加等级。
    public int GetRemovalThreshold(bool asUpgradedPreview = false) =>
        RemovalCombatThreshold + UpgradeThresholdBonus * CurrentUpgradeLevel;

    public int RemainingCombats => Math.Max(1, GetRemovalThreshold() - CombatsSeen);

    public override async Task AfterCombatEnd(CombatRoom room)
    {
        if (Pile?.Type != PileType.Deck)
            return;

        CombatsSeen++;
        if (CombatsSeen >= GetRemovalThreshold())
            await ReinhardRemovalTracking.RemoveAfterCombatLimitAsync(this);
    }
}

// 莱茵哈鲁特的“经过 x 场战斗后移除”是它自身的正常寿命结束，不能触发傲慢结局。
// 使用一个明确的短暂标记包住原版删牌命令；其余原版 RemoveFromDeck 调用则都视为
// 被其他效果移除了牌组。
internal static class ReinhardRemovalTracking
{
    private static int _automaticRemovalDepth;

    public static bool IsAutomaticRemoval => Volatile.Read(ref _automaticRemovalDepth) != 0;

    public static async Task RemoveAfterCombatLimitAsync(ReinhardCard card)
    {
        Interlocked.Increment(ref _automaticRemovalDepth);
        try
        {
            await CardPileCmd.RemoveFromDeck(new[] { card }, true);
        }
        finally
        {
            Interlocked.Decrement(ref _automaticRemovalDepth);
        }
    }

    public static async Task ShowPrideEndingAfterRemovalAsync(Task nativeRemoval)
    {
        await nativeRemoval;
        RouteState.EnterPrideRoute();
        await GrantHeartOfPrideAsync();
        PrideEndingOverlay.TryShow();
    }

    // 傲慢线的新机制：进入时授予“傲慢之心”——死亡诅咒变为 0 费可打出，
    // 打出时三选一升级牌进手牌。旧的“回归不加诅咒+血量+1”已删除（与强欲
    // 线重复），诅咒照常叠加，如今它们是可以打出去的燃料而非纯粹的负担。
    private static async Task GrantHeartOfPrideAsync()
    {
        try
        {
            var player = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault();
            if (player is null || player.Relics.Any(relic => relic is HeartOfPride))
                return;

            await RelicCmd.Obtain<HeartOfPride>(player);
            ModLog.Write("Pride ending entered; Heart of Pride granted.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Heart of Pride grant failed: {exception}");
        }
    }
}

// 莱茵哈鲁特的升级只允许来自火堆锻造。锻造流程（SmithRestSiteOption.OnSelect，
// 含其中的选卡 UI）运行期间置位上下文；其余升级来源（战斗内效果、事件等）
// 在选卡列表和升级入口两层都被剔除，局内升级对这张牌无效。
internal static class ReinhardUpgradeContext
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) != 0;

    public static void Begin() => Volatile.Write(ref _active, 1);

    public static void End() => Volatile.Write(ref _active, 0);
}

[HarmonyPatch(typeof(SmithRestSiteOption), nameof(SmithRestSiteOption.OnSelect))]
internal static class ReinhardSmithUpgradeContextPatch
{
    [HarmonyPrefix]
    private static void Prefix() => ReinhardUpgradeContext.Begin();

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result) =>
        __result = FinishInContextAsync(__result);

    private static async Task<bool> FinishInContextAsync(Task<bool> task)
    {
        try
        {
            return await task;
        }
        finally
        {
            ReinhardUpgradeContext.End();
        }
    }
}

// 非锻造来源的选卡升级列表剔除莱茵哈鲁特，玩家根本选不到它。
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade))]
internal static class ReinhardUpgradeSelectionFilterPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task<IEnumerable<CardModel>> __result)
    {
        if (ReinhardUpgradeContext.IsActive)
            return;

        __result = FilterOutReinhardAsync(__result);
    }

    private static async Task<IEnumerable<CardModel>> FilterOutReinhardAsync(
        Task<IEnumerable<CardModel>> task)
    {
        var cards = await task;
        var list = cards.Where(card => card is not ReinhardCard).ToList();
        if (list.Count != cards.Count())
            ModLog.Write("Removed Reinhard from a non-smith upgrade selection.");

        return list;
    }
}

// 非锻造来源的直接升级入口：单卡调用直接跳过，批量调用从列表中剔除，
// 升级对莱茵哈鲁特无效，其他卡牌不受影响。
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), new[] { typeof(CardModel), typeof(CardPreviewStyle) })]
internal static class ReinhardSingleUpgradeStripPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel card)
    {
        if (ReinhardUpgradeContext.IsActive || card is not ReinhardCard)
            return true;

        ModLog.Write("Blocked a non-smith upgrade on Reinhard.");
        return false;
    }
}

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), new[] { typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle) })]
internal static class ReinhardMultipleUpgradeStripPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref IEnumerable<CardModel> cards)
    {
        if (ReinhardUpgradeContext.IsActive || !cards.Any(card => card is ReinhardCard))
            return;

        cards = cards.Where(card => card is not ReinhardCard).ToList();
        ModLog.Write("Blocked a non-smith upgrade on Reinhard.");
    }
}

// RemoveFromDeck 是原版所有带动画的“从牌组删牌”入口。分别覆盖单卡和多卡
// 重载，以兼容删一张与批量删牌的效果；结局层只在原版删牌任务完成后建立，
// 不会阻塞原本的删牌结算或房间切换。
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(CardModel), typeof(bool) })]
internal static class ReinhardSingleDeckRemovalPridePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, out bool __state) =>
        __state = card is ReinhardCard && !ReinhardRemovalTracking.IsAutomaticRemoval;

    [HarmonyPostfix]
    private static void Postfix(bool __state, ref Task __result)
    {
        if (__state)
            __result = ReinhardRemovalTracking.ShowPrideEndingAfterRemovalAsync(__result);
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(IReadOnlyList<CardModel>), typeof(bool) })]
internal static class ReinhardMultipleDeckRemovalPridePatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards, out bool __state) =>
        __state = !ReinhardRemovalTracking.IsAutomaticRemoval &&
                  cards.Any(card => card is ReinhardCard);

    [HarmonyPostfix]
    private static void Postfix(bool __state, ref Task __result)
    {
        if (__state)
            __result = ReinhardRemovalTracking.ShowPrideEndingAfterRemovalAsync(__result);
    }
}

// 傲慢结局仅是一个不阻塞原版流程的顶层演出：图片和背景淡入，点击屏幕下半部分后
// 一同淡出。图片按比例居中；利用整体淡入淡出配合暗背景柔化了图片与场景的边界。
internal static class ReinhardCardPlayback
{
    public static async Task PlayAsync(CardModel card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var creature = card.Owner?.Creature;
        var combatState = card.CombatState;
        if (creature is null || combatState is null)
            return;

        // 标记给原版 OnPlayWrapper 的 EndCardOrPotionEffect 收尾补丁。
        // 这张卡在自动出牌时可能在攻击结束后留下一个尚未推进的胜利检查，
        // 因此要在原版卡牌效果完整收尾后再请求原版 EndCombatInternal。
        MarkSettlementPending();

        // 保留莱茵哈鲁特专属的华丽收场蓄力与命中演出；角色自身默认的攻击
        // 动作由 WithNoAttackerAnim 单独关闭，避免和专属演出重复。
        var anticipation = NGrandFinaleVfx.Create(creature);
        var combatRoom = NCombatRoom.Instance;
        if (anticipation is not null && combatRoom is not null)
            combatRoom.CombatVfxContainer.AddChild(anticipation);

        await Cmd.Wait(1.2f, false);

        await new AttackCommand(799m)
            .FromCard(card, cardPlay)
            .TargetingAllOpponents(combatState)
            .WithNoAttackerAnim()
            .WithHitVfxNode(enemy => NGrandFinaleImpactVfx.Create(enemy))
            .WithHitFx(null, null, "blunt_attack.mp3")
            .Execute(choiceContext);

        // 事件自动出牌发生在“结束回合”被拦截的窗口里：799 点伤害可能被
        // 格挡、减伤或超高生命吞掉而没有杀死全部敌人，战斗会卡在拦截的
        // 回合流程上无法继续。动画播完后把仍存活的怪物强制击杀，走原生
        // 死亡链，胜利结算由原版检查与 ReinhardCardEffectSettlementPatch
        // 收尾。玩家手动打出这张卡时不拦截回合流程，不在此列。
        if (ReinhardEventState.IsAutoPlayInProgress)
        {
            var survivors = combatState.Enemies.Where(enemy => enemy.IsAlive).ToList();
            if (survivors.Count > 0)
            {
                await CreatureCmd.Kill(survivors, true);
                ModLog.Write($"Reinhard auto-play force-killed {survivors.Count} surviving enemy(ies) after the damage animation.");
            }
        }

        // 只使用原版 AttackCommand 的伤害结算；胜利收尾由下面的原版
        // EndCardOrPotionEffect 补丁负责，避免绕过原版结算链。
    }

    private static int _settlementPending;

    public static void MarkSettlementPending() => Interlocked.Exchange(ref _settlementPending, 1);

    public static bool ConsumeSettlementPending() => Interlocked.Exchange(ref _settlementPending, 0) != 0;
}

// 莱茵哈鲁特的伤害会让所有敌人同时死亡。正常卡牌流程在绝大多数情况下会
// 自动完成胜利检查；如果这次出牌发生在回合结束拦截/自动行动链中，则在原版
// 卡牌效果收尾之后补一次原版结束方法，避免必须再打出下一张牌才能结算。
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCardOrPotionEffect))]
internal static class ReinhardCardEffectSettlementPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player, ref Task __result)
    {
        if (!ReinhardCardPlayback.ConsumeSettlementPending())
            return;

        __result = FinishNativeSettlementAsync(__result);
    }

    private static async Task FinishNativeSettlementAsync(Task nativeSettlement)
    {
        await nativeSettlement;

        var manager = CombatManager.Instance;
        if (!manager.IsInProgress || !manager.IsEnding)
            return;

        ModLog.Write("Reinhard native card effect ended with pending victory; finishing through EndCombatInternal.");
        // EndCombatInternal 是原版的内部方法，不能直接从 mod 程序集调用；
        // 通过 Harmony 自带的反射工具调用它，仍然走原版的完整结算链。
        var endCombat = AccessTools.Method(typeof(CombatManager), "EndCombatInternal", Type.EmptyTypes);
        if (endCombat?.Invoke(manager, null) is Task endTask)
            await endTask;
    }
}

// 运行时 mod 没有独立的本地化表。此前返回虚构的 ReturnByDeath LocString，
// 原版在把卡加入手牌时会先查表而抛出 LocException，导致卡停在左上角。
// 这里提供合法的原版 LocString；自定义文案在实际“按牌堆渲染描述”的入口覆写。
[HarmonyPatch(typeof(CardModel), nameof(CardModel.Description), MethodType.Getter)]
internal static class ReinhardDescriptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (__instance is ReinhardCard)
            __result = ModelDb.Card<GrandFinale>().Description;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.TitleLocString), MethodType.Getter)]
internal static class ReinhardTitleLocStringPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (__instance is ReinhardCard)
            __result = ModelDb.Card<GrandFinale>().TitleLocString;
    }
}

// 先让原版把关键词（包括“固有”的富文本样式）渲染出来，再保留该关键词所在的
// 第一段，只替换之后的华丽收场效果正文。这样无需给复杂的 LocManager 打补丁。
[HarmonyPatch]
internal static class ReinhardDescriptionRenderPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(CardModel)).Where(method =>
            method.Name is "GetDescriptionForPile" or "GetDescriptionForUpgradePreview");

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, MethodBase __originalMethod, ref string __result)
    {
        if (__instance is not ReinhardCard)
            return;

        const string innate = "固有";
        var reinhard = (ReinhardCard)__instance;
        // 锻造选卡界面的升级预览按升级后的阈值展示。
        var asUpgradedPreview = __originalMethod.Name == "GetDescriptionForUpgradePreview";
        var remaining = Math.Max(1, reinhard.GetRemovalThreshold() - reinhard.CombatsSeen);
        var effect = $"对所有敌人造成799伤害。经过{remaining}场战斗后从牌组中移除。";
        var innateStart = __result.IndexOf(innate, StringComparison.Ordinal);
        if (innateStart < 0)
        {
            // 非中文或异常预览路径的保底文案；正常中文卡面会走下面的原生关键词保留分支。
            __result = "固有。" + effect;
            ModLog.Write("Reinhard description had no rendered Innate keyword; used the plain-text fallback.");
            return;
        }

        // 正常富文本会在关键词段后换行；若某个界面没有换行，则保留“固有。”
        // 所在的第一个句子，避免把华丽收场的条件说明一并保留。
        var firstLineBreak = __result.IndexOfAny(new[] { '\r', '\n' }, innateStart);
        var keywordEnd = firstLineBreak >= 0
            ? firstLineBreak + 1
            : Math.Min(__result.Length, __result.IndexOf('。', innateStart) + 1);
        if (keywordEnd <= innateStart)
            keywordEnd = innateStart + innate.Length;

        while (keywordEnd < __result.Length &&
               (__result[keywordEnd] == '\r' || __result[keywordEnd] == '\n'))
        {
            keywordEnd++;
        }

        __result = __result[..keywordEnd] + effect;
    }
}

[HarmonyPatch(typeof(CardModel), "OnPlay")]
internal static class ReinhardCardPlayPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
    {
        if (__instance is not ReinhardCard)
            return true;

        __result = ReinhardCardPlayback.PlayAsync(__instance, choiceContext, cardPlay);
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Title), MethodType.Getter)]
internal static class ReinhardTitlePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is ReinhardCard reinhard)
            __result = reinhard.UpgradeCount == 0
                ? "莱茵哈鲁特"
                : $"莱茵哈鲁特+{reinhard.UpgradeCount}";
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.PortraitPath), MethodType.Getter)]
internal static class ReinhardPortraitPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref string __result)
    {
        if (__instance is ReinhardCard)
            __result = ModelDb.Card<SwordSage>().PortraitPath;
    }
}

[HarmonyPatch(typeof(CardModel), "Pool", MethodType.Getter)]
internal static class ReinhardPoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not ReinhardCard)
            return true;

        __result = ModelDb.Card<MasterOfStrategy>().Pool;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "VisualCardPool", MethodType.Getter)]
internal static class ReinhardVisualPoolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel __instance, ref CardPoolModel __result)
    {
        if (__instance is not ReinhardCard)
            return true;

        __result = ModelDb.Card<MasterOfStrategy>().VisualCardPool;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedInCombat", MethodType.Getter)]
internal static class ReinhardCombatGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is ReinhardCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "CanBeGeneratedByModifiers", MethodType.Getter)]
internal static class ReinhardModifierGenerationPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is ReinhardCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "ShouldShowInCardLibrary", MethodType.Getter)]
internal static class ReinhardLibraryVisibilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is ReinhardCard)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), "IsPlayable", MethodType.Getter)]
internal static class ReinhardPlayablePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref bool __result)
    {
        if (__instance is ReinhardCard)
            __result = true;
    }
}

// 每场战斗记录结束回合次数。在原版进入敌方回合前，按原版伤害预览并模拟
// 回合交界处的格挡、持续伤害、球体被动和伤害遗物，判断本回合是否会致死。
