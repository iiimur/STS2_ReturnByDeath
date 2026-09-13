// 强欲结局相关的新遗物（只会在设计的特殊事件里出现，不进任何原生卡池）。
// 形状（贴图、稀有度）套用原生涅奥遗物，效果各自实现：
// - 过去的苦痛（套用涅奥的苦痛）：战斗开始时获得 1 层虚弱。
// - 现在的牺牲（套用涅奥的牺牲）：战斗开始时获得 1 层脆弱。
// - 未来的骨骸（套用涅奥的骨骸）：战斗开始时获得 1 层易伤。
// - 强欲之心（套用白银熔炉）：不再愧疚；每次死亡回归后生命/生命上限各 +2。
//   效果实现在 CheckpointStore.ApplyRecoveryState（回归流程），遗物本身是被动的。
// - 奥托的契约（套用拆信刀）：每场战斗第一回合将临时“奥托”加入手牌。
// - 怠惰之心（套用捕梦网）：获得即进入「怠惰」IF 线（RouteState.EnterSlothRoute）。
// 名称与描述走原生 "relics" 本地化表，在语言表每次加载后注入词条。

using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;

namespace ReturnByDeath;

public sealed class PainOfThePast : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "neows_torment";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        new[] { HoverTipFactory.FromPower<WeakPower>() };

    public override async Task AfterRoomEntered(AbstractRoom room)
    {
        if (room is not CombatRoom || Owner?.Creature is not { } owner)
            return;

        Flash();
        var applied = await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), owner, 1m, owner, null);
        GreedRelicEffects.FixFirstTurnDuration(applied);
    }
}

/// <summary>
/// 原生规则会给“施加于玩家的负面状态”设置 SkipNextDurationTick，跳过第一次
/// 回合结束衰减，导致战斗开始时施加的状态多持续一回合。这三个遗物的定位是
/// “仅本回合第一轮生效”，这里清除该标记，让状态在第一轮敌人行动结束后消失。
/// </summary>
internal static class GreedRelicEffects
{
    public static void FixFirstTurnDuration(PowerModel? applied)
    {
        if (applied is not null)
            applied.SkipNextDurationTick = false;
    }
}

public sealed class SacrificeOfThePresent : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "neows_sacrifice";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        new[] { HoverTipFactory.FromPower<FrailPower>() };

    public override async Task AfterRoomEntered(AbstractRoom room)
    {
        if (room is not CombatRoom || Owner?.Creature is not { } owner)
            return;

        Flash();
        var applied = await PowerCmd.Apply<FrailPower>(new ThrowingPlayerChoiceContext(), owner, 1m, owner, null);
        GreedRelicEffects.FixFirstTurnDuration(applied);
    }
}

public sealed class BonesOfTheFuture : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "neows_bones";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        new[] { HoverTipFactory.FromPower<VulnerablePower>() };

    public override async Task AfterRoomEntered(AbstractRoom room)
    {
        if (room is not CombatRoom || Owner?.Creature is not { } owner)
            return;

        Flash();
        var applied = await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(), owner, 1m, owner, null);
        GreedRelicEffects.FixFirstTurnDuration(applied);
    }
}

// 强欲之心：贴图套用白银熔炉（SilverCrucible）。被动效果在死亡回归流程
// （CheckpointStore）里实现：愧疚值固定为 0，回归后生命/生命上限各 +2。
public sealed class HeartOfGreed : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "silver_crucible";
}

// 奥托的契约：把“接受奥托后每场战斗第一回合手牌里出现临时奥托”的机制
// 遗物化（贴图套用拆信刀 LetterOpener，呼应契约主题）。实现完全照抄原生
// 璀璨珍珠（RadiantPearl）：重写 BeforeHandDraw，第 1 回合把生成的
// OttoCard 塞进手牌。与接受奥托的注入路径共用 OttoCardLifecycle 的每场
// 战斗一次计数，两者同时存在也不会叠出两张奥托。
public sealed class OttoContract : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "letter_opener";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        HoverTipFactory.FromCardWithCardHoverTips<OttoCard>();

    public override async Task BeforeHandDraw(Player player, PlayerChoiceContext choiceContext, ICombatState combatState)
    {
        if (player != Owner ||
            Owner?.PlayerCombatState is not { } playerCombat ||
            playerCombat.TurnNumber != 1 ||
            !OttoCardLifecycle.ShouldAddForRelic(Owner))
            return;

        Flash();
        var cards = new List<CardModel>
        {
            (Owner.Creature.CombatState ?? throw new InvalidOperationException("CombatState is unavailable for Otto's Contract."))
                .CreateCard<OttoCard>(Owner)
        };
        await CardPileCmd.AddGeneratedCardsToCombat(cards, PileType.Hand, Owner);
        ModLog.Write("Otto's Contract: temporary Otto added to the first combat hand.");
    }
}

// 怠惰之心：贴图套用彩虹戒指（RainbowRing）。获得即视为蕾姆事件选择了
// 红色“好的”——进入「怠惰」IF 线（全图开放、自由移动等路由效果全部
// 生效）。遗物本身写入检查点，死亡回归不消失；若在蕾姆事件里已经进过
// 怠惰线，拾取时不会重复触发。
public sealed class HeartOfSloth : RelicModel
{
    public override RelicRarity Rarity => RelicRarity.Ancient;

    protected override string IconBaseName => "rainbow_ring";

    public override async Task AfterObtained()
    {
        if (RouteState.IsSlothRoute)
            return;

        RouteState.EnterSlothRoute();
        ModLog.Write("Heart of Sloth obtained; the Sloth IF route begins.");
    }
}

// Mod 是纯 DLL（has_pck=false），没有自己的本地化文件；游戏原生支持
// LocTable.MergeWith 合并词条，这里在每次语言表加载完成后把遗物文本
// 注入原生 "relics" 表，保证语言切换后词条依然存在。
internal static class GreedRelicText
{
    private const string Table = "relics";

    private static readonly Dictionary<string, string> Entries = new()
    {
        ["PAIN_OF_THE_PAST.title"] = "过去的苦痛",
        ["PAIN_OF_THE_PAST.description"] = "战斗开始时获得[blue]1[/blue]层[gold]虚弱[/gold]。",
        ["PAIN_OF_THE_PAST.flavor"] = "无法被忘却的，终将成为重负。",

        ["SACRIFICE_OF_THE_PRESENT.title"] = "现在的牺牲",
        ["SACRIFICE_OF_THE_PRESENT.description"] = "战斗开始时获得[blue]1[/blue]层[gold]脆弱[/gold]。",
        ["SACRIFICE_OF_THE_PRESENT.flavor"] = "为了前进，总得留下些什么。",

        ["BONES_OF_THE_FUTURE.title"] = "未来的骨骸",
        ["BONES_OF_THE_FUTURE.description"] = "战斗开始时获得[blue]1[/blue]层[gold]易伤[/gold]。",
        ["BONES_OF_THE_FUTURE.flavor"] = "它属于一个尚未到来的你。",

        ["HEART_OF_GREED.title"] = "强欲之心",
        ["HEART_OF_GREED.description"] = "不再愧疚。每次死亡回归生命上限加[blue]2[/blue]。",
        ["HEART_OF_GREED.flavor"] = "想要的，从来都不只是活下去。",

        ["OTTO_CONTRACT.title"] = "奥托的契约",
        ["OTTO_CONTRACT.description"] = "每场战斗的第[blue]1[/blue]回合开始时，将[blue]1[/blue]张临时[gold]奥托[/gold]加入手牌。",
        ["OTTO_CONTRACT.flavor"] = "签名处永远空着——他从不催促，只等你开口。",

        ["HEART_OF_SLOTH.title"] = "怠惰之心",
        ["HEART_OF_SLOTH.description"] = "可以全图旅行。不再进行战斗。",
        ["HEART_OF_SLOTH.flavor"] = "想要一直，就这样休息下去。",

        ["HEART_OF_PRIDE.title"] = "傲慢之心",
        ["HEART_OF_PRIDE.description"] = "死亡诅咒（[gold]愧疚[/gold]、[gold]受伤[/gold]）可被打出，费用为[blue]0[/blue]。打出时，从三张升级后的随机牌中选择一张加入手牌，这张牌在本回合可被免费打出。",
        ["HEART_OF_PRIDE.flavor"] = "区区诅咒，也配束缚我？",
    };

    // 艾姬多娜事件（墓碑巨大花卉）的标题、开场描述、试验选项与结算页文字，注入原生 "events" 表。
    private static readonly Dictionary<string, string> EventEntries = new()
    {
        ["RBD_FLOWER.title"] = "艾姬多娜",

        ["RBD_FLOWER.INITIAL.description"] =
            "一朵[green]巨型花卉[/green]正生长在一座[red][jitter]骸骨堆积而成的山[/jitter][/red]上。\n\n" +
            "花瓣缓缓舒展，中心处传来令人[red]难以抗拒的吸引力[/red]，仿佛只要再靠近一步，就能窥见某个[aqua]不该知晓的答案[/aqua]。\n\n" +
            "你知道好奇心未必会带来好事，但[gold]答案[/gold]就在眼前。",

        ["RBD_FLOWER.REACH_DEEPER_1.description"] =
            "你向前迈出一步，周围的景象忽然变得模糊。那些早已过去的片段，一幕幕消失在身后，取而代之的是此刻正在发生的一切。\n\n" +
            "[purple]无法逃避，也无法重来。至少，这一刻还不行。[/purple]",

        ["RBD_FLOWER.REACH_DEEPER_2.description"] =
            "你继续向前。眼前的景象开始变得支离破碎，无数尚未发生的片段一闪而过。\n\n" +
            "你不知道哪一个会成为现实。但你知道，[purple]其中没有一个是安全的。[/purple]",

        ["RBD_FLOWER.TRIAL_1.title"] = "接受过去的试炼",
        ["RBD_FLOWER.TRIAL_1.description"] = "“首先要面对自己的过去。”获得[gold]过去的苦痛[/gold]。",
        ["RBD_FLOWER.TRIAL_1.result"] = "你把手伸进花蜜，取出了沉淀在往昔中的试炼之物——『过去的苦痛』。",

        ["RBD_FLOWER.TRIAL_2.title"] = "接受现在的试炼",
        ["RBD_FLOWER.TRIAL_2.description"] = "“好好看看不该存在的现在吧。”获得[gold]现在的牺牲[/gold]。",
        ["RBD_FLOWER.TRIAL_2.result"] = "花蜜之下是正在发生的代价。你接过了『现在的牺牲』。",

        ["RBD_FLOWER.TRIAL_3.title"] = "接受未来的试炼",
        ["RBD_FLOWER.TRIAL_3.description"] = "“面对终将来临的灾厄吧。”获得[gold]未来的骨骸[/gold]。",
        ["RBD_FLOWER.TRIAL_3.result"] = "在花的最深处，你取出了一副尚未属于任何人的骨骸——未来的你留下的。",

        // 最后分支的“强欲之心”选项（替换原生的花粉核心）。按设定这个选项
        // 理应致命（保留失去 7 点生命值的红字警告），实际暗中不扣血。
        ["RBD_FLOWER.HEART.title"] = "抵达核心",
        ["RBD_FLOWER.HEART.description"] = "失去[red]7[/red]点生命值。 获得[gold]强欲之心[/gold]。",
        ["RBD_FLOWER.HEART.result"] =
            "你纵身跃向花的中心——预期中的剧痛并没有降临。一颗[gold]心脏[/gold]在你的掌心安静地搏动，与花的脉动同频。\n\n" +
            "花没有枯萎。它只是，沉默了。",
    };

    // 注意：SetLanguageInternal 首次发生在 LocManager 构造函数内部，
    // 此时静态 Instance 还没赋值，必须用 Harmony 传入的实例，不能用单例。
    public static void Inject(LocManager? manager = null)
    {
        try
        {
            manager ??= LocManager.Instance;
            manager?.GetTable(Table).MergeWith(Entries);
            manager?.GetTable("events").MergeWith(EventEntries);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Greed relic text injection failed: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(LocManager), "SetLanguageInternal")]
internal static class GreedRelicTextReloadPatch
{
    [HarmonyPostfix]
    private static void Postfix(LocManager __instance) => GreedRelicText.Inject(__instance);
}
