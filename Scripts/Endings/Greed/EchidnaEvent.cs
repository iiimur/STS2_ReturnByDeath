// 艾姬多娜事件：通过地图“墓碑”按钮进入的巨大花卉（ColossalFlower）特殊化。
// 三个“采集花蜜”金币选项替换为“接受第 N 次试验”，分别获得三个强欲遗物：
//   初始页（35金币）        → 接受第一次试验 → 过去的苦痛
//   REACH_DEEPER_1 页（75） → 接受第二次试验 → 现在的牺牲
//   REACH_DEEPER_2 页（135）→ 接受第三次试验 → 未来的骨骸
// “深入探索”与原生的花粉核心（PollinousCore）分支保持原样。
// 只作用于墓碑进入的实例（TombstoneEntry.SpecialFlowerActive 标记），
// 原生随机问号房间里的巨大花卉不受影响。
// 事件内显示的标题被替换为“艾姬多娜”；试验获得的三个特殊遗物会立即
// 写入最新检查点，死亡回归后依然保留。

using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;

namespace ReturnByDeath;

[HarmonyPatch(typeof(ColossalFlower), "GenerateInitialOptions")]
internal static class EchidnaOptionsPatch
{
    [HarmonyPostfix]
    private static void Postfix(ColossalFlower __instance, ref IReadOnlyList<EventOption> __result)
    {
        if (!TombstoneEntry.SpecialFlowerActive)
            return;

        __result = EchidnaTrial.BuildInitialOptions(__instance);
        ModLog.Write("Echidna: initial options replaced with the three trials.");
    }
}

// 事件标题只在墓碑模式下改为“艾姬多娜”；原生巨大花卉不受影响。
[HarmonyPatch(typeof(EventModel), "Title", MethodType.Getter)]
internal static class EchidnaTitlePatch
{
    [HarmonyPostfix]
    private static void Postfix(EventModel __instance, ref LocString __result)
    {
        if (__instance is ColossalFlower && TombstoneEntry.SpecialFlowerActive)
            __result = new LocString("events", "RBD_FLOWER.title");
    }
}

// 开场描述只在墓碑模式下替换为自定义文案；原生巨大花卉不受影响。
[HarmonyPatch(typeof(EventModel), "InitialDescription", MethodType.Getter)]
internal static class EchidnaDescriptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(EventModel __instance, ref LocString __result)
    {
        if (__instance is ColossalFlower && TombstoneEntry.SpecialFlowerActive)
            __result = new LocString("events", "RBD_FLOWER.INITIAL.description");
    }
}

// 艾姬多娜事件的背景：墓碑模式下把事件立绘（原生 colossal_flower.png）
// 替换为 mod 目录下的“艾姬多娜-背景.png”。纹理只加载一次并缓存；
// 加载失败时回退为原生图，不影响事件运行。
[HarmonyPatch(typeof(EventModel), "CreateInitialPortrait")]
internal static class EchidnaPortraitPatch
{
    private const string BackgroundFileName = "艾姬多娜-背景.png";
    private static Texture2D? _background;

    [HarmonyPostfix]
    private static void Postfix(EventModel __instance, ref Texture2D __result)
    {
        if (__instance is not ColossalFlower || !TombstoneEntry.SpecialFlowerActive)
            return;

        var background = LoadBackground();
        if (background is not null)
            __result = background;
    }

    private static Texture2D? LoadBackground()
    {
        if (_background is not null)
            return _background;

        try
        {
            var path = ModAssetPaths.Image(BackgroundFileName);
            var image = Image.LoadFromFile(path);
            if (image is null)
            {
                ModLog.Write($"Echidna background image missing or unreadable: {path}");
                return null;
            }
            _background = ImageTexture.CreateFromImage(image);
            ModLog.Write($"Echidna background loaded: {image.GetWidth()}x{image.GetHeight()}.");
            return _background;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Echidna background load failed: {exception.Message}");
            return null;
        }
    }
}

internal static class EchidnaTrial
{
    public static List<EventOption> BuildInitialOptions(ColossalFlower flower)
    {
        // 已持有过去的苦痛：第一次试验不再出现，初始页只剩深入探索。
        var options = new List<EventOption>();
        if (!Holds<PainOfThePast>(flower))
            options.Add(TrialOption(flower, 1));
        options.Add(ReachDeeperOption(flower, 0));
        return options;
    }

    private static bool Holds<TRelic>(ColossalFlower flower) where TRelic : RelicModel =>
        flower.Owner?.Relics.Any(relic => relic is TRelic) == true;

    private static EventOption TrialOption(ColossalFlower flower, int trial)
    {
        var key = $"RBD_FLOWER.TRIAL_{trial}";
        // 防御式取词：词条缺失时回退为原始键，绝不抛异常炸掉事件界面。
        var title = flower.GetOptionTitle(key) ?? new LocString("events", $"{key}.title");
        var description = flower.GetOptionDescription(key) ?? new LocString("events", $"{key}.description");
        return new EventOption(flower, () => TrialAsync(flower, trial), title, description, key, TrialHoverTips(trial));
    }

    private static IEnumerable<IHoverTip> TrialHoverTips(int trial) => trial switch
    {
        1 => HoverTipFactory.FromRelic<PainOfThePast>(),
        2 => HoverTipFactory.FromRelic<SacrificeOfThePresent>(),
        _ => HoverTipFactory.FromRelic<BonesOfTheFuture>(),
    };

    private static async Task TrialAsync(ColossalFlower flower, int trial)
    {
        ModLog.Write($"Echidna: trial {trial} option chosen; granting relic.");
        var owner = flower.Owner ?? throw new InvalidOperationException("Echidna event has no owner.");
        RelicModel relic = trial switch
        {
            1 => await RelicCmd.Obtain<PainOfThePast>(owner),
            2 => await RelicCmd.Obtain<SacrificeOfThePresent>(owner),
            _ => await RelicCmd.Obtain<BonesOfTheFuture>(owner),
        };

        // 三个试验遗物是跨时间线的奖励：获得即写入最新检查点，死亡回归不消失。
        CheckpointStore.RecordEchidnaRelic(relic, owner);

        SetEventFinished(flower, new LocString("events", $"RBD_FLOWER.TRIAL_{trial}.result"));

        // 结算页点“继续”时清空牌组中的愧疚与受伤（带原生删牌动画）。
        EchidnaCurseCleanup.Mark(owner);
        ModLog.Write($"Echidna: trial {trial} completed; relic granted and persisted ({relic.Id}).");
    }

    private static EventOption ReachDeeperOption(ColossalFlower flower, int digs)
    {
        var key = digs == 0
            ? "COLOSSAL_FLOWER.pages.INITIAL.options.REACH_DEEPER_1"
            : $"COLOSSAL_FLOWER.pages.REACH_DEEPER_{digs}.options.REACH_DEEPER_{digs + 1}";
        return new EventOption(flower, () => ReachDeeperAsync(flower), key, Array.Empty<IHoverTip>())
            .ThatDoesDamage(ReachDeeperDamage(digs));
    }

    private static async Task ReachDeeperAsync(ColossalFlower flower)
    {
        var digs = GetDigs(flower);
        await DealReachDeeperDamage(flower, digs);
        SetDigs(flower, digs + 1);

        if (digs + 1 < 2)
        {
            // 已持有现在的牺牲：第二次试炼不再出现，本页只剩深入探索。
            var options = new List<EventOption>();
            if (!Holds<SacrificeOfThePresent>(flower))
                options.Add(TrialOption(flower, digs + 2));
            options.Add(ReachDeeperOption(flower, digs + 1));
            SetEventState(flower,
                new LocString("events", $"RBD_FLOWER.REACH_DEEPER_{digs + 1}.description"),
                options);
        }
        else
        {
            SetEventState(flower,
                new LocString("events", "RBD_FLOWER.REACH_DEEPER_2.description"),
                new List<EventOption> { TrialOption(flower, 3), HeartOfGreedOption(flower) });
        }
    }

    // 最后分支的“强欲之心”选项：替换原生的花粉核心。按设定跳向花心理应
    // 致命，因此保留伤害红字警告（ThatDoesDamage 只负责血量不足时闪红，
    // 不实际扣血）；实际暗中不扣血，选完仍能走到结算页的“继续”。
    private static EventOption HeartOfGreedOption(ColossalFlower flower)
    {
        var key = "RBD_FLOWER.HEART";
        var title = flower.GetOptionTitle(key) ?? new LocString("events", $"{key}.title");
        var description = flower.GetOptionDescription(key) ?? new LocString("events", $"{key}.description");
        return new EventOption(flower, () => HeartOfGreedAsync(flower), title, description, key,
                HoverTipFactory.FromRelic<HeartOfGreed>())
            .ThatDoesDamage(ReachDeeperDamage(2));
    }

    private static async Task HeartOfGreedAsync(ColossalFlower flower)
    {
        var owner = flower.Owner ?? throw new InvalidOperationException("Echidna event has no owner.");
        // 暗改：不调用 DealReachDeeperDamage，跳入花心不扣血。
        ModLog.Write("Echidna: Heart of Greed chosen; the Greed IF route begins (no damage taken).");
        await GrantHeartOfGreedAsync(owner);
        // 「强欲」IF 线触发演出：与傲慢/怠惰结局同款的羽化触发图淡入。
        GreedEndingOverlay.TryShow();
        SetEventFinished(flower, new LocString("events", "RBD_FLOWER.HEART.result"));
    }

    // 获得强欲之心的完整效果：授予遗物、写入检查点、清零诅咒预算、点亮线路、
    // 标记结算页的诅咒清理。控制台 boss greed 复用同一段代码（只是不播放结局演出）。
    internal static async Task GrantHeartOfGreedAsync(Player owner)
    {
        var relic = await RelicCmd.Obtain<HeartOfGreed>(owner);
        CheckpointStore.RecordEchidnaRelic(relic, owner);
        // 强欲线不再愧疚：预算立即清零，顶栏显示“愧疚 0 受伤 0”。
        CheckpointStore.CurseBudget.ClearToZero("Heart of Greed obtained");
        GreedIfState.Mark();
        EchidnaCurseCleanup.Mark(owner);
    }

    private static Task DealReachDeeperDamage(ColossalFlower flower, int digs) =>
        CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), flower.Owner!.Creature,
            ReachDeeperDamage(digs), ValueProp.Unblockable | ValueProp.Unpowered, null, null);

    // 原版每次深入的伤害值（5/6/7），反射读取以便跟随版本变化。
    private static decimal ReachDeeperDamage(int digs)
    {
        var damages = AccessTools.Field(typeof(ColossalFlower), "_prizeDamage")?.GetValue(null) as int[]
            ?? new[] { 5, 6, 7 };
        return damages[Math.Min(digs, damages.Length - 1)];
    }

    private static int GetDigs(ColossalFlower flower) =>
        Traverse.Create(flower).Property("NumberOfDigs").GetValue<int>();

    private static void SetDigs(ColossalFlower flower, int value) =>
        Traverse.Create(flower).Property("NumberOfDigs").SetValue(value);

    private static void SetEventState(EventModel model, LocString description, IEnumerable<EventOption> options) =>
        AccessTools.Method(typeof(EventModel), "SetEventState")?
            .Invoke(model, new object[] { description, options });

    private static void SetEventFinished(EventModel model, LocString description) =>
        AccessTools.Method(typeof(EventModel), "SetEventFinished")?
            .Invoke(model, new object[] { description });
}

// 试验结算页的“继续”按钮：原生流程是直接重开地图；这里在回地图之前
// 先播放原生删牌动画（CardPileCmd.RemoveFromDeck 的卡牌预览 + 移除特效），
// 把牌组里所有愧疚（Guilty）与受伤（Injury）清掉。标记只在拿走试验
// 遗物后设置，因此只有艾姬多娜的试验结算会触发，其余事件的“继续”
// 不受影响。
internal static class EchidnaCurseCleanup
{
    private static readonly Func<Task> _nativeProceed =
        AccessTools.MethodDelegate<Func<Task>>(AccessTools.Method(typeof(NEventRoom), "Proceed"));

    private static Player? _pendingOwner;

    public static void Mark(Player owner) => _pendingOwner = owner;

    [HarmonyPatch(typeof(NEventRoom), "Proceed")]
    internal static class ProceedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (_pendingOwner is not { } owner)
                return true;

            _pendingOwner = null;
            TaskHelper.RunSafely(RemoveCursesThenProceedAsync(owner));
            return false;
        }
    }

    private static async Task RemoveCursesThenProceedAsync(Player owner)
    {
        try
        {
            var curses = owner.Deck.Cards.Where(card => card is Guilty or Injury).ToList();
            if (curses.Count > 0)
            {
                await CardPileCmd.RemoveFromDeck(curses);
                ModLog.Write($"Echidna: removed {curses.Count} curse card(s) (Guilty/Injury) after the trial.");
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Echidna curse cleanup failed: {exception}");
        }

        await _nativeProceed();
    }
}

// 按钮强制启用：原生选项按钮要在入场动画的 tween 结束时才把 MouseFilter
// 设为 Stop，任何 tween 异常都会让按钮永远无法点击。这里在选项布局完成后
// 立即启用，并把按钮的 ProcessMode 设为 Always，保证即使某个祖先节点被
// 禁用（重入事件时出现过输入失灵），按钮依然能接收鼠标输入。
[HarmonyPatch(typeof(NEventLayout), "AddOptions")]
internal static class EchidnaButtonEnablePatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventLayout __instance)
    {
        try
        {
            foreach (var button in __instance.OptionButtons)
            {
                button.ProcessMode = Node.ProcessModeEnum.Always;
                button.Enable();
                button.EnableButton();
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Echidna button enable failed: {exception.Message}");
        }

        _ = ProbeOptionButtonsAsync(__instance);
    }

    // 命中探针：事件打开后持续采样选项按钮的输入状态与视口实际悬停控件，
    // 用于定位“按钮点不了”是覆盖、禁用还是矩形异常。状态变化才记录。
    private static async Task ProbeOptionButtonsAsync(NEventLayout layout)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;

            var last = "";
            for (var i = 0; i < 30 && TombstoneEntry.SpecialFlowerActive; i++)
            {
                await tree.ToSignal(tree.CreateTimer(0.5d), SceneTreeTimer.SignalName.Timeout);

                var button = layout?.OptionButtons?.FirstOrDefault();
                if (button is null || !GodotObject.IsInstanceValid(button))
                    continue;

                var hovered = button.GetViewport().GuiGetHoveredControl();
                var hoveredDesc = hovered is null ? "<none>" : $"{hovered.GetType().Name}:{hovered.Name}";
                var state = $"visible={button.IsVisibleInTree()} enabled={button.IsEnabled} " +
                    $"locked={button.Option?.IsLocked.ToString() ?? "?"} " +
                    $"rect={button.GlobalPosition}/{button.Size} filter={button.MouseFilter} hovered={hoveredDesc}";
                if (state == last)
                    continue;

                last = state;
                ModLog.Write($"Echidna probe: {state}");
            }
        }
        catch
        {
            // 纯诊断。
        }
    }
}

// 诊断埋点：确认点击是否到达按钮与事件房间，便于定位“点不了”发生在哪一层。
[HarmonyPatch(typeof(NEventOptionButton), "OnFocus")]
internal static class EchidnaHoverDiagnosticPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventOptionButton __instance)
    {
        try
        {
            if (!TombstoneEntry.SpecialFlowerActive)
                return;
            ModLog.Write($"Echidna diagnostics: button hovered (key={__instance.Option?.TextKey}).");
        }
        catch
        {
            // 纯诊断。
        }
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "OnPress")]
internal static class EchidnaPressDiagnosticPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventOptionButton __instance)
    {
        try
        {
            var option = __instance.Option;
            ModLog.Write($"Echidna diagnostics: button pressed (key={option?.TextKey}, locked={option?.IsLocked}, " +
                         $"filter={__instance.MouseFilter}, enabled={__instance.IsEnabled}, visible={__instance.IsVisibleInTree()}).");
        }
        catch
        {
            // 纯诊断，绝不能影响原生点击。
        }
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "OnRelease")]
internal static class EchidnaClickDiagnosticPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventOptionButton __instance)
    {
        try
        {
            var option = __instance.Option;
            ModLog.Write($"Echidna diagnostics: option button released (key={option?.TextKey}, locked={option?.IsLocked}).");
        }
        catch
        {
            // 纯诊断，绝不能影响原生点击。
        }
    }
}

[HarmonyPatch(typeof(NEventLayout), "DisableEventOptions")]
internal static class EchidnaDisableDiagnosticPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            ModLog.Write("Echidna diagnostics: DisableEventOptions called.");
        }
        catch
        {
            // 纯诊断。
        }
    }
}

[HarmonyPatch(typeof(NEventRoom), "OptionButtonClicked")]
internal static class EchidnaClickRoomDiagnosticPatch
{
    [HarmonyPostfix]
    private static void Postfix(EventOption option)
    {
        try
        {
            ModLog.Write($"Echidna diagnostics: room received click (key={option.TextKey}).");
        }
        catch
        {
            // 纯诊断。
        }
    }
}
