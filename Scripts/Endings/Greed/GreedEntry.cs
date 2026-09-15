// 强欲结局的入口。第二层地图图例里出现一个沙堡图标按钮（套用原生遗物
// “沙堡”的图标，悬浮放大、点击进入），点击进入占位的原生“巨大花卉”
// （ColossalFlower）问号事件，即艾姬多娜事件。
//
// 这是特殊事件：完全绕开地图旅行流程，不调用 AppendToMapPointHistory，
// 也不写本 mod 的遭遇日志，因此不会出现在地图历史与遭遇预告里。
// 事件结束后的原生 Proceed 会直接重新打开地图，路线与楼层号不受影响。

using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace ReturnByDeath;

[HarmonyPatch(typeof(NMapScreen), "Open")]
internal static class TombstoneButtonOpenPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        // 地图重新打开意味着墓碑事件已经结束（或本层开始）：复位特殊事件标记。
        TombstoneEntry.ClearSpecialFlower();
        TombstoneEntry.Ensure(__instance);
        // 沙堡按钮出现期间禁止前进：玩家必须先进入艾姬多娜事件。
        if (TombstoneEntry.IsButtonVisible)
            __instance.SetTravelEnabled(false);
    }
}

// 沙堡按钮可见期间（且地图已显示），原生流程随后调用的“恢复旅行”
// 一律否决，防止绕过事件直接前进。地图不可见时的恢复调用不拦：
// 原生“继续”流程是先 SetTravelEnabled(true) 再 Open，若误杀会导致
// 事件结束后旅行开关永远关闭（无法前进的旧 bug）。
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled), new[] { typeof(bool) })]
internal static class TombstoneTravelVetoPatch
{
    [HarmonyPrefix]
    private static void Prefix(NMapScreen __instance, ref bool enabled)
    {
        if (!enabled || !TombstoneEntry.IsButtonVisible || !__instance.IsVisibleInTree())
            return;

        enabled = false;
        ModLog.Write("Travel vetoed while the Echidna tombstone is showing.");
    }
}

internal static class TombstoneEntry
{
    private const int GreedActIndex = 1; // 第二层。

    private static NMapLegendItem? _button;
    private static bool _entering;

    // 墓碑按钮出现的大前提：二层的先古之民选项已完成并存档（Done 后
    // 由 AncientEventDonePatch 置位）。按局持久化，重启游戏不丢失，
    // 新开一局清除。
    internal static class Act2AncientState
    {
        private sealed class StateFile
        {
            public string? RunKey { get; set; }
            public bool Done { get; set; }
        }

        private static readonly string StatePath = Path.Combine(
            ModLog.ModDirectory, "return-by-death.act2-ancient.json");
        private static readonly object Sync = new();

        public static bool IsDone
        {
            get
            {
                lock (Sync)
                {
                    try
                    {
                        if (!CheckpointStore.TryLoad(out var checkpoint))
                            return false;
                        var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath));
                        return file is not null && file.Done &&
                               file.RunKey == CheckpointStore.GetRunKey(checkpoint);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public static void MarkDone()
        {
            lock (Sync)
            {
                try
                {
                    if (!CheckpointStore.TryLoad(out var checkpoint))
                        return;

                    File.WriteAllText(StatePath, JsonSerializer.Serialize(new StateFile
                    {
                        RunKey = CheckpointStore.GetRunKey(checkpoint),
                        Done = true,
                    }));
                    ModLog.Write("Act-2 Ancient completed and saved; the Echidna tombstone may now appear.");
                }
                catch (Exception exception)
                {
                    ModLog.Write($"Act-2 Ancient state save failed: {exception.Message}");
                }
            }
        }

        public static void ResetForNewRun()
        {
            try { File.Delete(StatePath); }
            catch (Exception exception) { ModLog.Write($"Could not clear act-2 ancient state: {exception.Message}"); }
        }
    }

    // 墓碑进入的艾姬多娜事件（巨大花卉特殊化）是否处于激活状态
    // （试验选项替换金币选项、标题改名）。在墓碑点击时置位，
    // 地图重新打开（事件结束/新的一层）时清除。
    private static int _specialFlowerActive;
    private static int _specialGreedOnly;

    public static bool SpecialFlowerActive => Volatile.Read(ref _specialFlowerActive) != 0;

    public static bool SpecialGreedOnly => Volatile.Read(ref _specialGreedOnly) != 0;

    public static void ClearSpecialFlower()
    {
        Volatile.Write(ref _specialFlowerActive, 0);
        Volatile.Write(ref _specialGreedOnly, 0);
    }

    // 沙堡按钮当前是否可见（仅地图打开期间有意义）。
    internal static bool IsButtonVisible =>
        _button is not null && GodotObject.IsInstanceValid(_button) && _button.Visible;

    public static void Ensure(NMapScreen mapScreen)
    {
        try
        {
            var runManager = RunManager.Instance;
            var state = runManager?.DebugOnlyGetState();
            var specialOpen = state is not null && IsOttoRejectEchidnaOpen(state);
            var normalOpen = state is not null && Act2AncientState.IsDone && IsTombstoneOpen(state);
            var shouldShow = runManager is { IsSingleplayerOrFakeMultiplayer: true } &&
                state is not null &&
                state.CurrentActIndex == GreedActIndex &&
                (normalOpen || specialOpen);

            if (_button is not null && GodotObject.IsInstanceValid(_button))
            {
                UpdateLayout();
                RefreshLegendTextOnExistingButton();
                _button.Visible = shouldShow;
                return;
            }

            if (!shouldShow)
                return;

            var items = mapScreen.GetNodeOrNull<Control>("%MapLegend")?.GetNodeOrNull<Control>("LegendItems");
            var reference = items?.GetChildren().OfType<NMapLegendItem>()
                .FirstOrDefault(item => !item.Name.ToString().StartsWith("@") && !ReferenceEquals(item, _button));
            if (items is null || reference is null)
            {
                ModLog.Write("Tombstone button skipped: no native legend item to clone.");
                return;
            }

            // 直接克隆原生图例条目：图标尺寸、MegaLabel 字体与配色全部原生继承。
            // 克隆体保留原生节点名（_Ready 会按名字做本地化并校验，非法名字会
            // 抛异常），加入场景树后立刻覆盖为沙堡图标与“？？？”文本。
            var clone = (NMapLegendItem)reference.Duplicate();
            items.AddChild(clone);
            var icon = clone.GetNode<TextureRect>("Icon");
            icon.Texture = LoadSandCastleIcon();
            // 强制图标等比适配绘制，再把整个图标节点缩放到与旧版一致的 34px
            // 显示尺寸（原生图例格子偏大，直接填满会显得过大）。
            icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            icon.PivotOffset = icon.Size / 2f;
            var iconRect = MathF.Max(MathF.Max(icon.Size.X, icon.Size.Y), 1f);
            _iconRestScale = 34f / iconRect;
            icon.Scale = new Vector2(_iconRestScale, _iconRestScale);
            clone.GetNode<MegaLabel>("MegaLabel").SetTextAutoSize("？？？");
            UpdateLegendText();
            clone.Enable();
            clone.MouseFilter = Control.MouseFilterEnum.Stop;
            clone.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnTombstonePressed()));
            _button = clone;
            UpdateLayout();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Tombstone button ensure failed: {exception}");
        }
    }

    // 克隆图例条目的悬浮行为已由 EchidnaLegendFocusPatch 接管：
    // 保留图标放大/还原，去掉悬浮提示（NHoverTipSet 定位）与地图节点高亮。
    internal static NMapLegendItem? LegendButton => _button;

    private static Tween? _iconTween;

    // 图标节点的常态缩放：把原生格子里等比适配的沙堡图标缩到 34px 显示尺寸。
    private static float _iconRestScale = 1f;

    // target 为倍率：1f = 常态（_iconRestScale），1.25f = 悬浮放大。
    public static void AnimateIconScale(float multiplier)
    {
        if (_button is null || !GodotObject.IsInstanceValid(_button))
            return;

        var icon = _button.GetNodeOrNull<TextureRect>("Icon");
        if (icon is null)
            return;

        icon.PivotOffset = icon.Size / 2f;
        var rest = new Vector2(_iconRestScale, _iconRestScale);
        _iconTween?.Kill();
        _iconTween = icon.CreateTween();
        if (multiplier > 1f)
        {
            icon.Scale = rest * multiplier;
        }
        else
        {
            _iconTween.TweenProperty(icon, "scale", rest, 0.5f)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Expo);
        }
    }

    private static Texture2D? LoadSandCastleIcon()
    {
        try
        {
            var path = ModelDb.Relic<SandCastle>().PackedIconPath;
            var texture = ResourceLoader.Load<Texture2D>(path);
            if (texture is null)
                ModLog.Write($"Sand castle icon could not be loaded: {path}");
            return texture;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sand castle icon load failed: {exception.Message}");
            return null;
        }
    }

    // 图例文字：第一次进入艾姬多娜事件前显示“？？？”，之后固定为“艾姬多娜”。
    // 以持久化文件跨局记忆“是否已经进入过”。
    private static void UpdateLegendText()
    {
        if (_button is null || !GodotObject.IsInstanceValid(_button))
            return;

        _button.GetNode<MegaLabel>("MegaLabel")
            .SetTextAutoSize(EchidnaVisitState.HasVisited ? "艾姬多娜" : "？？？");
    }

    private static void RefreshLegendTextOnExistingButton()
    {
        if (_button is not null && GodotObject.IsInstanceValid(_button))
            UpdateLegendText();
    }

    // 墓碑开放规则（按持有进度收紧血量区间，保证三次进入都能活着走出
    // 深入探索的扣血，且三个遗物必须分三次、以不同血量档进入才能集齐）：
    //   持有未来的骨骸或强欲之心          → 隐藏（已集齐/已进强欲线，防止重复拿心）
    //   持有现在的牺牲：11 < 血量 <= 18    → 开放（两次深入共扣 11 点）
    //   持有过去的苦痛： 5 < 血量 <= 11    → 开放（一次深入扣 5 点）
    //   未持有任何试验遗物：血量 <= 5      → 开放（只能拿走第一次试验）
    //   其余情况                          → 隐藏
    private static bool IsTombstoneOpen(RunState state)
    {
        var player = state.Players.FirstOrDefault();
        if (player is null)
            return false;

        var relics = player.Relics;
        var hp = player.Creature.CurrentHp;
        if (relics.Any(relic => relic is BonesOfTheFuture or HeartOfGreed))
            return false;
        if (relics.Any(relic => relic is SacrificeOfThePresent))
            return hp > 11 && hp <= 18;
        if (relics.Any(relic => relic is PainOfThePast))
            return hp > 5 && hp <= 11;
        return hp <= 5;
    }

    // 拒绝奥托后，原本的“死档”在第二层受伤预算达到 5 时打开一条隐藏
    // 的艾姬多娜入口。它不要求先完成第二层先古事件，且强欲之心已经拿到
    // 后不再重复出现。
    private static bool IsOttoRejectEchidnaOpen(RunState? state)
    {
        if (state is null || state.CurrentActIndex != GreedActIndex ||
            !CheckpointStore.TryLoad(out var checkpoint) ||
            !AbandonRunVideo.WasRejectedForRun(checkpoint))
            return false;

        var player = state.Players.FirstOrDefault();
        if (player is null || player.Relics.Any(relic => relic is HeartOfGreed))
            return false;

        return CheckpointStore.CurseBudget.Current.Injuries >= 5;
    }

    // 作为图例的一部分挂在 LegendItems 里：尺寸与位置对齐现有图例条目，
    // 排在其列表末尾。若 LegendItems 本身是布局容器，则由容器接管排列。
    private static void UpdateLayout()
    {
        if (_button is null || !GodotObject.IsInstanceValid(_button))
            return;

        var items = _button.GetParent() as Control;
        if (items is null)
            return;

        Control? reference = null;
        foreach (var child in items.GetChildren())
        {
            if (child is Control control && !ReferenceEquals(control, _button))
                reference = control;
        }

        if (reference is not null)
        {
            _button.CustomMinimumSize = reference.Size;
            _button.Position = reference.Position + new Vector2(0f, reference.Size.Y);
        }
        _button.PivotOffset = _button.Size / 2f;
    }

    private static async void OnTombstonePressed()
    {
        if (_entering)
            return;

        var runManager = RunManager.Instance;
        var state = runManager?.DebugOnlyGetState();
        if (runManager is null || state is null || state.CurrentActIndex != GreedActIndex)
            return;

        // 正在进行的战斗中禁止点击：此时进入事件会直接跳过整场战斗。
        // 战斗胜利后的结算阶段不拦——IsInProgress 在战斗结束时即关闭，
        // 而 CurrentCombatId 要等房间退出才清空；在其他事件中进入同样
        // 放行（会跳过当前事件，属于玩家自己的取舍）。
        if (CombatManager.Instance.CurrentCombatId is not null && CombatManager.Instance.IsInProgress)
        {
            ModLog.Write("Tombstone press ignored: combat is in progress.");
            return;
        }

        // 关闭残留的奖励覆盖层：原生流程进入新房间时会顺带关闭它，而墓碑
        // 进入绕过了这一步——残留的全屏遮罩（RewardContainerMask）虽不可见
        // 却会吃掉事件房间的全部鼠标输入（“第二次进入点不了按钮”的根源）。
        var overlayStack = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance;
        while (overlayStack?.Peek() is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen lingering)
        {
            overlayStack.Remove(lingering);
            ModLog.Write("Closed a lingering rewards overlay before entering the Echidna event.");
        }

        _entering = true;
        try
        {
            var specialGreedOnly = IsOttoRejectEchidnaOpen(state);
            ModLog.Write(specialGreedOnly
                ? "Tombstone pressed after rejecting Otto; entering the direct Greed branch."
                : "Tombstone pressed; entering the placeholder Colossal Flower event.");
            Volatile.Write(ref _specialFlowerActive, 1);
            Volatile.Write(ref _specialGreedOnly, specialGreedOnly ? 1 : 0);
            await runManager.FadeOut();
            NMapScreen.Instance?.Close(animateOut: false);
            runManager.CombatStateSynchronizer.StartSync();
            await runManager.CombatStateSynchronizer.WaitForSync();

            var room = new EventRoom(ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.ColossalFlower>());
            await runManager.EnterRoom(room);
            // 成功进入艾姬多娜事件：记录“已进入过”，图例文字从“？？？”变为“艾姬多娜”。
            EchidnaVisitState.Mark();
            UpdateLegendText();
            _ = TaskHelper.RunSafely(runManager.FadeIn());
        }
        catch (Exception exception)
        {
            ModLog.Write($"Tombstone event entry failed: {exception}");
            _ = TaskHelper.RunSafely(runManager.FadeIn());
        }
        finally
        {
            _entering = false;
        }
    }
}

// 本局是否进入过艾姬多娜事件：按局持久化（带 run key）——存档重载/重启
// 游戏后依然是“艾姬多娜”；死亡回归保留（run key 不变）；新开一局清除。
internal static class EchidnaVisitState
{
    private sealed class StateFile
    {
        public string? RunKey { get; set; }
        public bool Visited { get; set; }
    }

    private static readonly string StatePath = Path.Combine(
        ModLog.ModDirectory, "return-by-death.echidna-visit.json");
    private static readonly object Sync = new();

    public static bool HasVisited
    {
        get
        {
            lock (Sync)
            {
                try
                {
                    if (!CheckpointStore.TryLoad(out var checkpoint))
                        return false;
                    var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath));
                    return file is not null && file.Visited &&
                           file.RunKey == CheckpointStore.GetRunKey(checkpoint);
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public static void Mark()
    {
        lock (Sync)
        {
            try
            {
                if (!CheckpointStore.TryLoad(out var checkpoint))
                    return;

                File.WriteAllText(StatePath, JsonSerializer.Serialize(new StateFile
                {
                    RunKey = CheckpointStore.GetRunKey(checkpoint),
                    Visited = true,
                }));
            }
            catch (Exception exception)
            {
                ModLog.Write($"Echidna visit state save failed: {exception.Message}");
            }
        }
    }

    public static void ResetForNewRun()
    {
        try { File.Delete(StatePath); }
        catch (Exception exception) { ModLog.Write($"Could not clear echidna visit state: {exception.Message}"); }
    }
}

// 「强欲」IF 线标记：在艾姬多娜事件拿到强欲之心时点亮（仅内存，新开一局
// 重置；死亡回归不清除）。强欲之心遗物本身写入检查点，跨回归持续存在，
// 后续强欲线的剧情与效果都以本标记或遗物持有为准。
internal static class GreedIfState
{
    private static int _entered;

    public static bool HasEntered => Volatile.Read(ref _entered) != 0;

    public static void Mark()
    {
        Volatile.Write(ref _entered, 1);
    }

    public static void ResetForNewRun() => Volatile.Write(ref _entered, 0);
}

// 克隆图例条目的悬浮接管：跳过原生 OnFocus/OnUnfocus（原生会弹出定位的
// 悬浮提示、高亮地图节点，且 Enable 时可能把图标卡在放大态），改为只在
// 悬浮时放大图标、离开时还原。
[HarmonyPatch(typeof(NMapLegendItem), "OnFocus")]
internal static class EchidnaLegendFocusPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapLegendItem __instance)
    {
        if (!ReferenceEquals(__instance, TombstoneEntry.LegendButton))
            return true;

        TombstoneEntry.AnimateIconScale(1.25f);
        return false;
    }
}

[HarmonyPatch(typeof(NMapLegendItem), "OnUnfocus")]
internal static class EchidnaLegendUnfocusPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapLegendItem __instance)
    {
        if (!ReferenceEquals(__instance, TombstoneEntry.LegendButton))
            return true;

        TombstoneEntry.AnimateIconScale(1f);
        return false;
    }
}
