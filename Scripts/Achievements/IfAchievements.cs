// IF 成就系统：记录并展示本 mod 各 IF 线结局与关键事件分支的成就。
// 成就全局持久化（跨局累计，不随死亡回归清除）；入口在主菜单
// “百科大全”底部的“IF成就”按钮。

using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace ReturnByDeath;

internal static class IfAchievements
{
    public sealed class Achievement
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    // 结局成就。
    public static readonly Achievement[] EndingAchievements =
    {
        new() { Id = "pride_ending", Name = "傲慢结局", Description = "见证骄傲之男的终局。" },
        new() { Id = "sloth_ending", Name = "怠惰结局", Description = "见证怠惰之罪的终局。" },
        new() { Id = "greed_ending", Name = "强欲结局", Description = "达成强欲终局：第三层的第二次死亡，或是离开那个宝箱。" },
    };

    // 事件成就。
    public static readonly Achievement[] EventAchievements =
    {
        new() { Id = "reinhard_rescue", Name = "剑圣的援手", Description = "在致命的敌方回合前得到莱茵哈鲁特的救援。" },
        new() { Id = "otto_accept", Name = "朋友的护符", Description = "接受了奥托的提议。" },
        new() { Id = "otto_reject", Name = "另一条路", Description = "拒绝了奥托的提议。" },
        new() { Id = "echidna_past", Name = "过去的试炼", Description = "在艾姬多娜事件中通过过去的试炼。" },
        new() { Id = "echidna_present", Name = "现在的试炼", Description = "在艾姬多娜事件中通过现在的试炼。" },
        new() { Id = "echidna_future", Name = "未来的试炼", Description = "在艾姬多娜事件中通过未来的试炼。" },
        new() { Id = "rem_reward", Name = "从零开始", Description = "在蕾姆事件中选择“不了”，带着她的祝福继续前进。" },
    };

    private static readonly string StatePath = Path.Combine(
        ModLog.ModDirectory, "return-by-death.if-achievements.json");
    private static readonly object Sync = new();
    private static HashSet<string>? _unlocked;

    public static bool IsUnlocked(string id)
    {
        lock (Sync)
        {
            return EnsureLoaded().Contains(id);
        }
    }

    // 幂等解锁：已达成时不写文件、不刷日志。
    public static void Unlock(string id)
    {
        lock (Sync)
        {
            var unlocked = EnsureLoaded();
            if (!unlocked.Add(id))
                return;

            try
            {
                File.WriteAllText(StatePath, JsonSerializer.Serialize(unlocked));
                ModLog.Write($"IF achievement unlocked: {id}.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"IF achievement save failed ({id}): {exception.Message}");
            }
        }
    }

    // 控制台测试用切换：已解锁时隐藏，未解锁时解锁。
    // 返回切换后的状态，true=已解锁，false=已隐藏。
    public static bool Toggle(string id)
    {
        lock (Sync)
        {
            var unlocked = EnsureLoaded();
            var isUnlocked = !unlocked.Remove(id);
            if (isUnlocked)
                unlocked.Add(id);

            try
            {
                File.WriteAllText(StatePath, JsonSerializer.Serialize(unlocked));
                ModLog.Write($"IF achievement {(isUnlocked ? "unlocked" : "hidden")} by console: {id}.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"IF achievement toggle save failed ({id}): {exception.Message}");
            }

            return isUnlocked;
        }
    }

    private static HashSet<string> EnsureLoaded()
    {
        if (_unlocked is not null)
            return _unlocked;
        try
        {
            _unlocked = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath)) ?? new HashSet<string>();
        }
        catch (Exception exception)
        {
            ModLog.Write($"IF achievements load failed; starting empty: {exception.Message}");
            _unlocked = new HashSet<string>();
        }
        return _unlocked;
    }

    // 百科大全的展示面板。
    public static void ShowPanel()
    {
        CanvasLayer? layer = null;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                return;

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            layer = new CanvasLayer
            {
                Name = "ReturnByDeathIfAchievements",
                Layer = 10060,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(layer);

            var dim = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.78f),
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            layer.AddChild(dim);

            var panel = new PanelContainer
            {
                Position = viewportSize / 2f - new Vector2(560f, 420f),
                CustomMinimumSize = new Vector2(1120f, 840f)
            };
            layer.AddChild(panel);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 40);
            margin.AddThemeConstantOverride("margin_right", 40);
            margin.AddThemeConstantOverride("margin_top", 28);
            margin.AddThemeConstantOverride("margin_bottom", 24);
            panel.AddChild(margin);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 14);
            margin.AddChild(root);

            root.AddChild(MakeLabel("IF成就", 52, Colors.Gold));
            root.AddChild(MakeLabel("记录你在各个 IF 线与关键事件中抵达过的分支。", 24, new Color(0.7f, 0.75f, 0.85f)));

            var scroll = new ScrollContainer
            {
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0f, 600f)
            };
            root.AddChild(scroll);
            var list = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            list.AddThemeConstantOverride("separation", 10);
            scroll.AddChild(list);

            list.AddChild(MakeLabel("—— 结局成就 ——", 30, new Color(0.55f, 0.75f, 0.95f)));
            foreach (var achievement in EndingAchievements)
                list.AddChild(MakeAchievementRow(achievement));
            list.AddChild(MakeLabel("—— 事件成就 ——", 30, new Color(0.55f, 0.75f, 0.95f)));
            foreach (var achievement in EventAchievements)
                list.AddChild(MakeAchievementRow(achievement));

            var closeButton = new Button { Text = "关闭", CustomMinimumSize = new Vector2(160f, 56f) };
            closeButton.Pressed += () =>
            {
                if (GodotObject.IsInstanceValid(layer))
                    layer.QueueFree();
            };
            var center = new CenterContainer();
            center.AddChild(closeButton);
            root.AddChild(center);
        }
        catch (Exception exception)
        {
            if (layer is not null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            ModLog.Write($"IF achievement panel failed: {exception}");
        }
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Control MakeAchievementRow(Achievement achievement)
    {
        var unlocked = IsUnlocked(achievement.Id);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);

        var mark = MakeLabel(unlocked ? "★" : "☆", 34, unlocked ? Colors.Gold : new Color(0.45f, 0.45f, 0.5f));
        row.AddChild(mark);

        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        // 未解锁时不透露成就名称和触发条件，整行只显示一个“？？？”。
        var name = MakeLabel(unlocked ? achievement.Name : "？？？", 30,
            unlocked ? Colors.Gold : new Color(0.62f, 0.62f, 0.66f));
        column.AddChild(name);
        if (unlocked)
        {
            var description = MakeLabel(achievement.Description, 22, new Color(0.85f, 0.87f, 0.92f));
            column.AddChild(description);
        }
        row.AddChild(column);
        return row;
    }
}

// 百科大全入口：在底部按钮行克隆一个“IF成就”按钮，点击打开展示面板。
[HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
internal static class CompendiumIfAchievementsButtonPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCompendiumSubmenu __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<Control>("%IfAchievementsButton") is not null)
                return;

            var statisticsButton = AccessTools.Field(typeof(NCompendiumSubmenu), "_statisticsButton")
                ?.GetValue(__instance) as NButton;
            if (statisticsButton is null)
                return;

            // 不复制原“角色数据”按钮已连接的 Released 信号，
            // 否则点击克隆按钮时还会同时打开原统计页。
            const int duplicateFlags = (int)(Node.DuplicateFlags.Groups |
                Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            var button = (NButton)statisticsButton.Duplicate(duplicateFlags);
            button.Name = "IfAchievementsButton";
            // Duplicate 会复制节点，却仍共享 BgPanel 上的 ShaderMaterial。
            // 两个按钮的 OnFocus 都会修改材质亮度，因此必须在入树前给
            // IF 成就按钮一份独立材质，彻底隔离双方的悬浮/按下动画。
            var bgPanel = button.GetNodeOrNull<Control>("BgPanel");
            if (bgPanel?.Material is ShaderMaterial sharedMaterial)
                bgPanel.Material = (Material)sharedMaterial.Duplicate(true);
            // Duplicate 后直接从克隆节点下取文字，避免它继续显示
            // 原统计按钮的“角色数据”。
            var label = button.GetNodeOrNull<MegaLabel>("Label")
                ?? button.FindChildren("*", nameof(MegaLabel), true, false).OfType<MegaLabel>().FirstOrDefault();
            label?.SetTextAutoSize("IF成就");

            var parent = statisticsButton.GetParent();
            parent.AddChild(button);
            button.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => IfAchievements.ShowPanel()));

            // 克隆按钮沿用统计按钮的布局：纵向与统计按钮并排由容器接管，
            // 手动把它排到运行历史按钮的右侧。
            if (parent is Control container)
            {
                button.Position = statisticsButton.Position + new Vector2(statisticsButton.Size.X + 16f, 0f);
                button.Size = statisticsButton.Size;
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"IF achievements compendium button failed: {exception}");
        }
    }
}
