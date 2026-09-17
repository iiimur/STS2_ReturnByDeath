// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace ReturnByDeath;

/// <summary>
/// 「愤怒之心」：贴图套用原版「骇人头盔」（IntimidatingHelmet）。规则糅合了怀表
/// （Pocketwatch，统计本回合打出的牌数）与奥利哈钢（Orichalcum，在回合结束的
/// 极早期钩子里给予格挡）：本回合打出的牌 ≤ 1 张时，在回合结束时获得 999 格挡。
///
/// 时机与计数都对齐两个原生参考遗物：
/// - 牌数用 AfterCardPlayed 累加，BeforeSideTurnStart 清零（与怀表一致）。
/// - 条件判定放在 BeforeSideTurnEndVeryEarly，早于 Plating 等回合结束效果；
///   实际给予格挡放在 BeforeSideTurnEnd，与奥利哈钢相同。
/// - 数值在“回合结束的极早期”一次性快照并缓存，避免与任何回合计数的结算
///   顺序产生偏差（本回合打出的牌在正常流程下不会在回合结束后继续变化）。
/// </summary>
public sealed class HeartOfWrath : RelicModel
{
    private bool _shouldTrigger;

    // 本回合已打出的牌数，显示在遗物计数上。
    private int _cardsPlayedThisTurn;

    // 回合结束判定时快照的“本回合牌数”。SnapshotTaken 保证同一次回合结束
    // 里只快照一次；跨保存读档的重建模型会归零，等同于新回合的初始状态。
    private int _cardsPlayedLastTurn;

    public override RelicRarity Rarity => RelicRarity.Ancient;

    // 套用原版「骇人头盔」的图标。
    protected override string IconBaseName => "intimidating_helmet";

    // 与怀表一致：战斗中显示本回合已打出的牌数。
    public override bool ShowCounter => CombatManager.Instance.IsInProgress;

    public override int DisplayAmount => _cardsPlayedThisTurn;

    [SavedProperty]
    public bool SnapshotTaken { get; set; }

    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new DynamicVar[]
        {
            new BlockVar(999m, ValueProp.Unpowered),
            new DynamicVar("CardThreshold", 1m)
        };

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        new[] { HoverTipFactory.Static(StaticHoverTip.Block) };

    private bool ShouldTrigger
    {
        get => _shouldTrigger;
        set
        {
            AssertMutable();
            _shouldTrigger = value;
        }
    }

    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner != Owner || !CombatManager.Instance.IsInProgress)
            return Task.CompletedTask;

        _cardsPlayedThisTurn++;
        RefreshCounter();
        return Task.CompletedTask;
    }

    // 在回合结束的极早期定格本回合牌数并判定条件（此时先于 Plating 等效果）。
    public override Task BeforeSideTurnEndVeryEarly(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (!participants.Contains(Owner.Creature))
            return Task.CompletedTask;

        if (!SnapshotTaken)
        {
            _cardsPlayedLastTurn = _cardsPlayedThisTurn;
            SnapshotTaken = true;
        }

        if (_cardsPlayedLastTurn <= DynamicVars["CardThreshold"].BaseValue)
            ShouldTrigger = true;

        return Task.CompletedTask;
    }

    public override async Task BeforeSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (!ShouldTrigger)
            return;

        ShouldTrigger = false;
        Flash();
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, null);
    }

    public override Task BeforeSideTurnStart(
        PlayerChoiceContext choiceContext, CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        if (!participants.Contains(Owner.Creature))
            return Task.CompletedTask;

        _cardsPlayedThisTurn = 0;
        SnapshotTaken = false;
        ShouldTrigger = false;
        RefreshCounter();
        return Task.CompletedTask;
    }

    public override Task AfterCombatEnd(CombatRoom _)
    {
        _cardsPlayedThisTurn = 0;
        _cardsPlayedLastTurn = 0;
        SnapshotTaken = false;
        ShouldTrigger = false;
        Status = RelicStatus.Normal;
        InvokeDisplayAmountChanged();
        return Task.CompletedTask;
    }

    private void RefreshCounter()
    {
        Status = _cardsPlayedThisTurn <= DynamicVars["CardThreshold"].BaseValue
            ? RelicStatus.Active
            : RelicStatus.Normal;
        InvokeDisplayAmountChanged();
    }
}
