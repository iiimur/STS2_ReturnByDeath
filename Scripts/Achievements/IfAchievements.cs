// IF 成就系统：记录并展示本 mod 各 IF 线结局与关键事件分支的成就。
// 成就全局持久化（跨局累计，不随死亡回归清除）；入口在主菜单
// “百科大全”底部的“IF成就”按钮。

using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Models.Relics;

namespace ReturnByDeath;

internal static class IfAchievements
{
    public sealed class Achievement
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public Type DisplayRelicType { get; init; } = typeof(HeartOfPride);

        public string TitleLocKey => $"RBD_IF_ACHIEVEMENT.{Id}.title";
        public string DescriptionLocKey => $"RBD_IF_ACHIEVEMENT.{Id}.description";
    }

    // 结局成就。
    public static readonly Achievement[] EndingAchievements =
    {
        new() { Id = "pride_ending", Name = "傲慢结局", Description = "见证骄傲之男的终局。", DisplayRelicType = typeof(HeartOfPride) },
        new() { Id = "sloth_ending", Name = "怠惰结局", Description = "见证怠惰之罪的终局。", DisplayRelicType = typeof(HeartOfSloth) },
        new() { Id = "greed_ending", Name = "强欲结局", Description = "达成强欲终局：第三层的第二次死亡，或是离开那个宝箱。", DisplayRelicType = typeof(HeartOfGreed) },
        new() { Id = "wrath_ending", Name = "愤怒结局", Description = "见证愤怒之罪的终局。", DisplayRelicType = typeof(HeartOfWrath) },
    };

    // 事件成就。
    public static readonly Achievement[] EventAchievements =
    {
        // 剑圣与拒绝奥托没有专属事件遗物，分别借用最贴近分支意象的
        // 原生玉石之剑与涅奥护符作为成就图标；悬浮文字仍显示成就本身。
        new() { Id = "reinhard_rescue", Name = "剑圣的援手", Description = "在致命的敌方回合前得到莱茵哈鲁特的救援。", DisplayRelicType = typeof(SwordOfJade) },
        new() { Id = "otto_accept", Name = "朋友的护符", Description = "接受了奥托的提议。", DisplayRelicType = typeof(OttoContract) },
        new() { Id = "echidna_past", Name = "过去的试炼", Description = "在艾姬多娜事件中通过过去的试炼。", DisplayRelicType = typeof(PainOfThePast) },
        new() { Id = "echidna_present", Name = "现在的试炼", Description = "在艾姬多娜事件中通过现在的试炼。", DisplayRelicType = typeof(SacrificeOfThePresent) },
        new() { Id = "echidna_future", Name = "未来的试炼", Description = "在艾姬多娜事件中通过未来的试炼。", DisplayRelicType = typeof(BonesOfTheFuture) },
        new() { Id = "rem_reward", Name = "从零开始", Description = "在蕾姆事件中选择“不了”，带着她的祝福继续前进。", DisplayRelicType = typeof(ForgottenSoul) },
    };

    public static IEnumerable<KeyValuePair<string, string>> LocalizationEntries
    {
        get
        {
            yield return new KeyValuePair<string, string>("RBD_IF_ENDINGS.title", "结局成就");
            yield return new KeyValuePair<string, string>("RBD_IF_EVENTS.title", "事件成就");
            foreach (var achievement in EndingAchievements.Concat(EventAchievements))
            {
                yield return new KeyValuePair<string, string>(achievement.TitleLocKey, achievement.Name);
                yield return new KeyValuePair<string, string>(achievement.DescriptionLocKey, achievement.Description);
            }
        }
    }

    private static readonly string StatePath = ModLog.StateFile("return-by-death.if-achievements.json");
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

    public static int HideAll(IEnumerable<Achievement> achievements)
    {
        lock (Sync)
        {
            var unlocked = EnsureLoaded();
            var hidden = 0;
            foreach (var achievement in achievements)
            {
                if (unlocked.Remove(achievement.Id))
                    hidden++;
            }

            if (hidden == 0)
                return 0;

            try
            {
                File.WriteAllText(StatePath, JsonSerializer.Serialize(unlocked));
                ModLog.Write($"IF achievements hidden by category command: {hidden}.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"IF achievement category hide failed: {exception.Message}");
            }

            return hidden;
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

    // 直接进入原生遗物收集子页面；具体内容由下面的 LoadRelics 补丁替换。
    public static void ShowPanel(NCompendiumSubmenu compendium)
    {
        try
        {
            var stack = AccessTools.Field(typeof(NSubmenu), "_stack")?.GetValue(compendium) as NSubmenuStack;
            if (stack is null)
                throw new InvalidOperationException("The compendium submenu stack is unavailable.");

            var collection = stack.GetSubmenuType<NRelicCollection>();
            IfAchievementRelicCollection.Arm(collection);
            stack.Push(collection);
        }
        catch (Exception exception)
        {
            ModLog.Write($"IF achievement collection failed to open: {exception}");
        }
    }
}

// IF 成就页复用原生 NRelicCollection：页面背景、滚动条、分组标题、遗物格、
// 未发现遮罩、悬浮提示和返回按钮全部沿用游戏实现，只替换本次打开时的数据源。
internal static class IfAchievementRelicCollection
{
    private static readonly MethodInfo ClearRelicsMethod =
        AccessTools.Method(typeof(NRelicCollection), "ClearRelics")
        ?? throw new MissingMethodException(typeof(NRelicCollection).FullName, "ClearRelics");
    private static readonly MethodInfo LoadSubcategoryMethod =
        AccessTools.Method(typeof(NRelicCollectionCategory), "LoadSubcategory")
        ?? throw new MissingMethodException(typeof(NRelicCollectionCategory).FullName, "LoadSubcategory");
    private static readonly MethodInfo GetModelMethod =
        AccessTools.Method(typeof(ModelDb), "Get", new[] { typeof(Type) })
        ?? throw new MissingMethodException(typeof(ModelDb).FullName, "Get(Type)");
    private static NRelicCollection? _armedCollection;

    public static void Arm(NRelicCollection collection)
    {
        _armedCollection = collection;
    }

    public static bool IsArmed(NRelicCollection collection) =>
        ReferenceEquals(_armedCollection, collection);

    public static Task Load(NRelicCollection collection)
    {
        ClearRelicsMethod.Invoke(collection, null);

        var categories = GetCategories(collection);
        foreach (var category in categories)
            category.Visible = false;

        PopulateCategory(collection, categories[0], "RBD_IF_ENDINGS.title", IfAchievements.EndingAchievements);
        PopulateCategory(collection, categories[1], "RBD_IF_EVENTS.title", IfAchievements.EventAchievements);

        foreach (var scroll in collection.FindChildren("*", nameof(ScrollContainer), true, false).OfType<ScrollContainer>())
            scroll.ScrollVertical = 0;

        ModLog.Write("Opened IF achievements with the native relic-collection layout.");
        return Task.CompletedTask;
    }

    public static void PrepareNative(NRelicCollection collection)
    {
        foreach (var category in GetCategories(collection))
            category.Visible = true;
    }

    public static void OnClosed(NRelicCollection collection)
    {
        if (!ReferenceEquals(_armedCollection, collection))
            return;

        _armedCollection = null;
        PrepareNative(collection);
    }

    public static IfAchievements.Achievement? GetAchievementFor(RelicModel relic) =>
        _armedCollection is null
            ? null
            : IfAchievements.EndingAchievements
                .Concat(IfAchievements.EventAchievements)
                .FirstOrDefault(achievement => achievement.DisplayRelicType == relic.GetType());

    private static NRelicCollectionCategory[] GetCategories(NRelicCollection collection)
    {
        var categories = new[]
        {
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Starter"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Common"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Uncommon"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Rare"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Shop"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Ancient"),
            collection.GetNodeOrNull<NRelicCollectionCategory>("%Event"),
        };

        if (categories.Any(category => category is null))
            throw new InvalidOperationException("The native relic-collection categories are unavailable.");

        return categories!;
    }

    private static void PopulateCategory(
        NRelicCollection collection,
        NRelicCollectionCategory category,
        string titleLocKey,
        IReadOnlyList<IfAchievements.Achievement> achievements)
    {
        category.Visible = true;
        var models = achievements
            .Select(achievement => GetModelMethod.Invoke(null, new object[] { achievement.DisplayRelicType }) as RelicModel
                ?? throw new InvalidOperationException($"Achievement display relic is unavailable: {achievement.DisplayRelicType.FullName}"))
            .ToList();

        // LoadSubcategory 的两个集合依次表示“已见过”和“当前可解锁”。
        // 所有成就都属于可解锁项；只有真正达成的进入“已见过”集合，其他项
        // 因而使用原生 NotSeen 外观与“？？？”悬浮文字，而不是泄露名称。
        var visibleModels = achievements
            .Select((achievement, index) => (achievement, model: models[index]))
            .Where(pair => IfAchievements.IsUnlocked(pair.achievement.Id))
            .Select(pair => pair.model)
            .ToHashSet();
        var availableModels = models.ToHashSet();

        LoadSubcategoryMethod.Invoke(category, new object[]
        {
            collection,
            new LocString("relics", titleLocKey),
            models,
            visibleModels,
            availableModels,
        });

    }
}

[HarmonyPatch(typeof(NRelicCollection), "LoadRelics")]
internal static class IfAchievementRelicCollectionLoadPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NRelicCollection __instance, ref Task __result)
    {
        IfAchievementRelicCollection.PrepareNative(__instance);
        if (!IfAchievementRelicCollection.IsArmed(__instance))
            return true;

        __result = IfAchievementRelicCollection.Load(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(NRelicCollection), nameof(NRelicCollection.OnSubmenuClosed))]
internal static class IfAchievementRelicCollectionClosePatch
{
    [HarmonyPostfix]
    private static void Postfix(NRelicCollection __instance) =>
        IfAchievementRelicCollection.OnClosed(__instance);
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Title), MethodType.Getter)]
internal static class IfAchievementRelicTitlePatch
{
    [HarmonyPostfix]
    private static void Postfix(RelicModel __instance, ref LocString __result)
    {
        var achievement = IfAchievementRelicCollection.GetAchievementFor(__instance);
        if (achievement is not null)
            __result = new LocString("relics", achievement.TitleLocKey);
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicDescription), MethodType.Getter)]
internal static class IfAchievementRelicDescriptionPatch
{
    [HarmonyPostfix]
    private static void Postfix(RelicModel __instance, ref LocString __result)
    {
        var achievement = IfAchievementRelicCollection.GetAchievementFor(__instance);
        if (achievement is not null)
            __result = new LocString("relics", achievement.DescriptionLocKey);
    }
}

// 遗物详情页（NInspectRelicScreen）用 SaveManager.IsRelicSeen 决定展示真实资料
// 还是“未发现”占位。成就图标借用的原生遗物大多已被玩家在本局里真正拿到过，
// 因此 IsRelicSeen 为 true，点击未解锁的成就就会直接泄露名称、描述与风味文本。
// 在 IF 成就页打开期间，把未解锁成就一律视为“未见过”，详情页便走原生的
// UNDISCOVERED 分支（图标压暗、名称与描述替换为未知占位）。
// 该方法是全游戏唯一的调用点就在这里，所以在非成就页不受影响。
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IsRelicSeen))]
internal static class IfAchievementRelicSeenPatch
{
    [HarmonyPostfix]
    private static void Postfix(RelicModel relic, ref bool __result)
    {
        var achievement = IfAchievementRelicCollection.GetAchievementFor(relic);
        if (achievement is not null)
            __result = IfAchievements.IsUnlocked(achievement.Id);
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
                Callable.From<NButton>(_ => IfAchievements.ShowPanel(__instance)));

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
