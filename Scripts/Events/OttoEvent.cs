// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook), nameof(MegaCrit.Sts2.Core.Hooks.Hook.BeforeHandDraw))]
internal static class OttoBeforeHandDrawPatch
{
    [HarmonyPostfix]
    private static void Postfix(ICombatState combatState, Player player,
        PlayerChoiceContext playerChoiceContext, ref Task __result)
    {
        if (!OttoCardLifecycle.ShouldAddToFirstHand(combatState, player))
            return;

        var originalHookTask = __result;
        __result = AddOttoAfterNativeHookAsync(originalHookTask, combatState, player);
    }

    private static async Task AddOttoAfterNativeHookAsync(
        Task originalHookTask, ICombatState combatState, Player player)
    {
        await originalHookTask;
        await OttoCardLifecycle.AddToFirstHandAsync(player);
    }
}

// 傲慢线最终 Boss 战开场对白。
// BeforeHandDraw 位于战斗初始化的发牌动作之前：先让原版的发牌前钩子完成，
// 再播放两段对白；该 Task 完成后，原版才会继续计算手牌数量并正式发牌。
internal static class OttoPendingCleanup
{
    private const string MarkerContent = "return-by-death-otto-pending-cleanup-v1";
    // 接受奥托重载后，等页面稳定可见再展示回血与删牌动画；需要调手感时只改这里。
    private const double PostLoadCleanupDelaySeconds = 2.0d;
    private static readonly string MarkerPath = ModLog.StateFile("return-by-death.otto-pending-cleanup");
    private static int _applying;

    public static void Mark()
    {
        try { File.WriteAllText(MarkerPath, MarkerContent); }
        catch (Exception exception) { ModLog.Write($"Could not persist Otto cleanup marker: {exception.Message}"); }
    }

    public static bool IsPending
    {
        get
        {
            try { return File.ReadAllText(MarkerPath) == MarkerContent; }
            catch { return false; }
        }
    }

    public static void Clear()
    {
        try { File.Delete(MarkerPath); } catch { }
        Volatile.Write(ref _applying, 0);
    }

    public static async Task ApplyAfterLoadAsync(Task loadTask)
    {
        try
        {
            await loadTask;
            if (!IsPending || Interlocked.Exchange(ref _applying, 1) != 0)
                return;

            // 等待载入后的下一帧，让地图、事件或战斗页面完成挂载；
            // 随后额外等待 2 秒，再调用原生回血/删牌命令展示对应动画。
            // 计时器跑在 Godot 场景线程，避免 Task.Delay 回调越过游戏主线程。
            await Task.Yield();
            var tree = Engine.GetMainLoop() as SceneTree
                ?? throw new InvalidOperationException("SceneTree is unavailable for Otto cleanup delay.");
            var delayTimer = tree.CreateTimer(PostLoadCleanupDelaySeconds, true, false, true);
            await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitSignal(
                delayTimer,
                SceneTreeTimer.SignalName.Timeout,
                tree.Root);
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state is null)
                throw new InvalidOperationException("RunState is unavailable after Otto reload.");

            var deckCount = 0;
            var combatCount = 0;
            foreach (var player in state.Players)
            {
                // 接受奥托后若血量上限不足初始值，先补到 86 再回满；
                // 上限已高于 86 的保持不变。
                if (player.Creature.MaxHp < CheckpointStore.InitialMaxHealth)
                    player.Creature.SetMaxHpInternal(CheckpointStore.InitialMaxHealth);

                // 接受奥托的回血改为发生在重载后的战斗/运行页面中，避免在
                // 选择按钮处直接改血而看不到动画。playAnim=true 使用原版回血动画。
                var missingHealth = player.Creature.MaxHp - player.Creature.CurrentHp;
                if (missingHealth > 0m)
                    await CreatureCmd.Heal(player.Creature, missingHealth, true);

                // 接受奥托的代价清理：牌组与战斗牌堆中的所有愧疚和受伤都移除。
                var deckWounds = player.Deck.Cards.Where(card => card is Injury or Guilty).ToArray();
                if (deckWounds.Length > 0)
                {
                    await CardPileCmd.RemoveFromDeck(deckWounds, true);
                    deckCount += deckWounds.Length;
                }

                var combatWounds = player.Piles
                    .Where(pile => pile.IsCombatPile)
                    .SelectMany(pile => pile.Cards.Where(card => card is Injury or Guilty))
                    .Distinct()
                    .ToArray();
                if (combatWounds.Length > 0)
                {
                    await CardPileCmd.RemoveFromCombat(combatWounds, false);
                    combatCount += combatWounds.Length;
                }
            }

            await SaveManager.Instance.SaveRun(state.CurrentRoom, true);
            // 奥托事件触发的节点完成后落一次检查点：问号事件里触发则立即存
            // （事件完成前，事件房间以 preFinishedRoom 保存，回归回到事件当时
            // 的页面）；战斗中触发则等该场战斗结算时再存（原生存档已在上面
            // 持久化，SL 不会回退到奥托之前的时间线）。
            if (state.CurrentRoom is CombatRoom)
            {
                OttoSettlementCheckpoint.MarkPending();
                ModLog.Write("Otto event happened in combat; checkpoint will be captured at settlement.");
            }
            else if (state.CurrentRoom is EventRoom)
            {
                CheckpointStore.CaptureCurrentAct(RunManager.Instance!, state.CurrentRoom);
                ModLog.Write("Otto event checkpoint captured before the event completes.");
            }
            Clear();
            ModLog.Write($"Otto reload cleanup completed: removed {deckCount} deck guilty/wounds and {combatCount} combat-pile guilty/wounds.");
        }
        catch (Exception exception)
        {
            // 保留标记，下一次重新载入时仍会尝试清理，避免出现只回血但受伤牌未移除的半完成状态。
            Volatile.Write(ref _applying, 0);
            ModLog.Write($"Otto reload cleanup failed: {exception}");
        }
    }
}

// 奥托事件在战斗中触发时，检查点要等该场战斗结算再落。用持久化 marker 记录
// 待结算状态：跨存读档存活（接受奥托后先退出再打完同一场战斗也能正确落点），
// 死亡回归与新开局时清除（时间线作废）。
internal static class OttoSettlementCheckpoint
{
    private const string MarkerContent = "return-by-death-otto-checkpoint-pending-v1";
    private static readonly string MarkerPath = ModLog.StateFile("return-by-death.otto-checkpoint-pending");

    public static void MarkPending()
    {
        try { File.WriteAllText(MarkerPath, MarkerContent); }
        catch (Exception exception) { ModLog.Write($"Could not persist the Otto checkpoint marker: {exception.Message}"); }
    }

    public static bool ConsumePending()
    {
        try
        {
            if (File.ReadAllText(MarkerPath) != MarkerContent)
                return false;

            File.Delete(MarkerPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Clear()
    {
        try { File.Delete(MarkerPath); } catch { }
    }
}

// 战斗结算（奖励页）时落奥托检查点：此时战斗房间已是 pre-finished 状态，
// 带 preFinishedRoom 保存，回归直接回到结算页；奖励卡的一致性由
// RewardSnapshotStore 保证。同时再持久化一次原生存档，确保在结算页
// 存档退出也不会回退到奥托之前的时间线。
[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
internal static class OttoCheckpointSettlementPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result) =>
        __result = CaptureAfterSettlementAsync(__result);

    private static async Task CaptureAfterSettlementAsync(Task settlementTask)
    {
        var pending = OttoSettlementCheckpoint.ConsumePending();
        await settlementTask;
        if (!pending)
            return;

        try
        {
            var runManager = RunManager.Instance;
            var state = runManager.DebugOnlyGetState();
            if (state?.CurrentRoom is not CombatRoom room)
                return;

            CheckpointStore.CaptureCurrentAct(runManager, room);
            ModLog.Write("Otto event checkpoint captured at combat settlement.");
            await SaveManager.Instance.SaveRun(state.CurrentRoom, true);
            ModLog.Write("Otto settlement native save persisted.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Otto settlement checkpoint failed: {exception}");
        }
    }
}
