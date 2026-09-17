// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace ReturnByDeath;

[HarmonyPatch(typeof(NRunHistory), "OnLeftButtonButtonReleased")]
internal static class AmnesiaMemoryPreviousArrowPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NRunHistory __instance) =>
        !AmnesiaMemoryPanel.TryHandleNativeHistoryArrow(__instance, -1);
}

[HarmonyPatch(typeof(NRunHistory), "OnRightButtonButtonReleased")]
internal static class AmnesiaMemoryNextArrowPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NRunHistory __instance) =>
        !AmnesiaMemoryPanel.TryHandleNativeHistoryArrow(__instance, 1);
}


// 失忆事件：第三层第一次死亡触发的特殊回归（傲慢 IF 线上不触发）。触发后该次回归——
//   卡组重置为开局初始状态（第一层先古选项之前，含高进阶的进阶之灾）；
//   不添加回归诅咒；
//   遭遇预告隐藏、地图（足迹/路线）不记录、总时间隐藏；
//   回归音效使用首次回归的音效。
// 死亡前的遭遇记录、卡组与总时间都保存在本文件里（不删除、只是不显示），
// 供左上角头像按钮查看，也是后续“找回记忆”事件的数据来源。
// 下一次死亡回归恢复正常：遭遇预告从当前层重新记录，地图继续记录，
// 总时间从失忆回归点起按新时间线重新计时（旧时间线的累计不带回）。
internal static class AmnesiaMemoryPanel
{
    private static NTopBarPortrait? _portrait;
    private static Control? _panelRoot;
    private static NRunHistory? _historyView;
    private static int _episodeIndex;
    private static int _open;

    public static void EnsureButton()
    {
        try
        {
            var run = NRun.Instance;
            if (run is null)
                return;

            if (_portrait is not null && GodotObject.IsInstanceValid(_portrait))
                return;

            var portrait = FindPortrait(run);
            if (portrait is null)
                return;

            _portrait = portrait;
            var catcher = new Control
            {
                Name = "ReturnByDeathMemoryButton",
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            catcher.GuiInput += input =>
            {
                if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    TogglePanel();
            };
            portrait.AddChild(catcher);
            ModLog.Write("Memory button attached to the top-bar portrait.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Memory button setup failed: {exception}");
        }
    }

    private static NTopBarPortrait? FindPortrait(Node root)
    {
        if (root is NTopBarPortrait portrait)
            return portrait;

        foreach (var child in root.GetChildren())
        {
            var found = FindPortrait(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static void TogglePanel()
    {
        if (Interlocked.Exchange(ref _open, 1) != 0)
        {
            ClosePanel();
            return;
        }

        try
        {
            if (NGame.Instance is null)
                throw new InvalidOperationException("NGame is unavailable for the memory panel.");

            // 面板挂进原版检查容器（卡牌/遗物检视屏幕所在的层）：
            // 点击卡牌弹出的检视屏幕会显示在面板之上，ESC 顺序也由原生输入链处理。
            var inspectionContainer = AccessTools.Field(typeof(NGame), "_inspectionContainer")?
                .GetValue(NGame.Instance) as Control
                ?? throw new InvalidOperationException("Inspection container is unavailable.");

            _panelRoot = new Control
            {
                Name = "ReturnByDeathMemoryPanel",
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            _panelRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            inspectionContainer.AddChild(_panelRoot);

            // 背景遮罩：把面板后面的游戏画面压暗（约 20% 亮度），避免重合。
            var dim = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.8f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _panelRoot.AddChild(dim);

            // 直接复用原版“历史记录”整个界面：背景、排版、卡组合并与悬浮详情
            // 都是原版的；遗物、药水、徽章、种子等缺失数据显示为空区块。
            var runHistoryView = NRunHistory.Create()
                ?? throw new InvalidOperationException("NRunHistory scene is unavailable.");
            // 根节点放行鼠标：点击背景（非交互区域）的事件会落到未处理输入，
            // 由 EscapeCloseNode 转为关闭请求；卡牌、按钮等交互控件不受影响。
            runHistoryView.MouseFilter = Control.MouseFilterEnum.Pass;
            runHistoryView.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _panelRoot.AddChild(runHistoryView);
            _historyView = runHistoryView;
            AttachNativeBackButton(runHistoryView);
            _episodeIndex = Math.Max(0, AmnesiaState.GetEpisodeCount() - 1);
            ShowEpisode(_episodeIndex);

            // 移除用不到的控件：分享按钮、面板左上角的玩家头像。
            // 左右切换箭头保留：由 UpdateEpisodeNavigation 启用并接管切换记忆。
            RemoveUnusedControls(runHistoryView);

            // 先强制创建检视屏幕；若本局之前已创建过（点过任何卡牌），它排在
            // 面板之前，必须 MoveToFront 提到最上层，否则详情会被面板盖住。
            NGame.Instance.GetInspectCardScreen();
            NGame.Instance.InspectCardScreen?.MoveToFront();

            // 游戏左上角头像的位置盖一个点击层：打开状态下再点一次头像即关闭。
            if (_portrait is not null && GodotObject.IsInstanceValid(_portrait))
            {
                var portraitCatcher = new Control
                {
                    TooltipText = "关闭记忆面板",
                    MouseFilter = Control.MouseFilterEnum.Stop
                };
                portraitCatcher.GlobalPosition = _portrait.GlobalPosition;
                portraitCatcher.Size = _portrait.Size;
                portraitCatcher.GuiInput += input =>
                {
                    if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                        ClosePanel();
                };
                _panelRoot.AddChild(portraitCatcher);
            }

            // 与原版抽牌堆/弃牌堆页面相同的关闭机制：注册阻塞屏幕并把 ESC 绑定到
            // 关闭面板——ESC 只关面板，不会触发设置窗口。
            NHotkeyManager.Instance!.AddBlockingScreen(_panelRoot);
            NHotkeyManager.Instance!.PushHotkeyPressedBinding(MegaInput.cancel, ClosePanel);

            ModLog.Write("Memory panel opened (native run history view).");
        }
        catch (Exception exception)
        {
            ClosePanel();
            ModLog.Write($"Memory panel failed: {exception}");
        }
    }

    private static void ShowEpisode(int index)
    {
        if (_historyView is null || !GodotObject.IsInstanceValid(_historyView))
            return;

        var count = AmnesiaState.GetEpisodeCount();
        if (count > 0)
            index = Math.Clamp(index, 0, count - 1);
        else
            index = 0;

        // DisplayRun 为原版私有方法，通过反射调用；每次切换只替换历史数据，
        // 仍复用同一个原版历史记录界面和卡牌检视屏幕。
        AccessTools.Method(typeof(NRunHistory), "DisplayRun")?
            .Invoke(_historyView, new object[] { BuildMemoryHistory(index) });

        if (_historyView.GetNodeOrNull<RichTextLabel>("%DeathQuoteLabel") is { } deathQuote)
            deathQuote.Text = "菜月昴";

        var episode = AmnesiaState.GetEpisode(index);
        var native = episode?.DeathNativeSeconds ?? AmnesiaState.DeathNativeSeconds;
        var total = episode?.DeathPlaytimeSeconds ?? AmnesiaState.DeathPlaytimeSeconds;
        if (_historyView.GetNodeOrNull<MegaLabel>("%RunTimeLabel") is { } timeLabel)
        {
            timeLabel.SetTextAutoSize(native > 0
                ? $"{TimeFormatting.Format(native)} / {TimeFormatting.Format(total)}"
                : TimeFormatting.Format(total));
        }

        _episodeIndex = index;
        UpdateEpisodeNavigation();
        ModLog.Write($"Memory panel displayed death record {index + 1}/{count}.");
    }

    internal static bool TryHandleNativeHistoryArrow(NRunHistory history, int delta)
    {
        if (Volatile.Read(ref _open) == 0 || !ReferenceEquals(history, _historyView))
            return false;

        ChangeEpisode(delta);
        return true;
    }

    private static void ChangeEpisode(int delta)
    {
        var count = AmnesiaState.GetEpisodeCount();
        if (count <= 0)
            return;

        var next = Math.Clamp(_episodeIndex + delta, 0, count - 1);
        if (next == _episodeIndex)
            return;

        ShowEpisode(next);
    }

    // 记忆切换直接用原版历史页自带的左右箭头：它们在 NRunHistory._Ready 里
    // 默认禁用（原版语义是切永久历史里的不同对局，而面板直接 Create 出来
    // 没有可选对局），这里按当前记忆位置启用/禁用；点击事件由
    // AmnesiaMemoryPrevious/NextArrowPatch 接管为切换记忆。
    private static void UpdateEpisodeNavigation(int? countOverride = null)
    {
        if (_historyView is null || !GodotObject.IsInstanceValid(_historyView))
            return;
        var count = countOverride ?? AmnesiaState.GetEpisodeCount();
        // 与原版 RefreshAndSelectRun 的箭头行为一致：没有可切换的方向时
        // 直接隐藏箭头，而不是留在原地变灰。
        var prev = _historyView.GetNodeOrNull<NRunHistoryArrowButton>("LeftArrow");
        if (prev is not null && GodotObject.IsInstanceValid(prev))
        {
            var hasPrev = count > 0 && _episodeIndex > 0;
            prev.Visible = hasPrev;
            if (hasPrev)
                prev.SetEnabled(true);
            else
                prev.Disable();
        }
        var next = _historyView.GetNodeOrNull<NRunHistoryArrowButton>("RightArrow");
        if (next is not null && GodotObject.IsInstanceValid(next))
        {
            var hasNext = count > 0 && _episodeIndex < count - 1;
            next.Visible = hasNext;
            if (hasNext)
                next.SetEnabled(true);
            else
                next.Disable();
        }
    }

    private static void ClosePanel()
    {
        var panelRoot = _panelRoot;
        if (panelRoot is null)
            return;

        // 立即标记关闭并清空引用；实际拆除延后一帧：
        // 让触发本次关闭的输入在阻塞屏幕仍生效时处理完毕，
        // 否则“ESC 关闭面板”会连带触发设置窗口。
        _panelRoot = null;
        Volatile.Write(ref _open, 0);
        _historyView = null;
        _backButton = null;
        _episodeIndex = 0;
        _ = TearDownNextFrameAsync(panelRoot);
    }

    private static async Task TearDownNextFrameAsync(Control panelRoot)
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

            // 期间若重新打开了面板（新的绑定与阻塞屏幕已注册），跳过移除。
            if (_panelRoot is null)
            {
                NHotkeyManager.Instance?.RemoveHotkeyPressedBinding(MegaInput.cancel, ClosePanel);
                NHotkeyManager.Instance?.RemoveBlockingScreen(panelRoot);
            }

            if (GodotObject.IsInstanceValid(panelRoot))
                panelRoot.QueueFree();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Memory panel teardown failed: {exception}");
        }
    }

    // 原版界面里本次用不到的控件：分享按钮、面板左上角的玩家头像
    // （头像的数据在 DisplayRun/SelectPlayer 里已加载完毕，移除不影响展示）、
    // 遗物一栏（没有快照数据，也不需要）。左右箭头保留用于切换记忆。
    private static void RemoveUnusedControls(NRunHistory view)
    {
        FreeDescendantsOfType<NShareButton>(view);
        // 多记录切换会再次调用 DisplayRun；隐藏而不是 QueueFree，避免原版
        // DisplayRun 在切换记录时访问已经释放的遗物节点。
        if (view.GetNodeOrNull<Control>("%RelicHistory") is { } relicHistory)
            relicHistory.Visible = false;
        if (view.GetNodeOrNull<Control>("%PlayerIconContainer") is { } iconContainer)
            iconContainer.Visible = false;
    }

    private static void FreeDescendantsOfType<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T target)
                target.QueueFree();
            FreeDescendantsOfType<T>(child);
        }
    }

    private static NBackButton? _backButton;

    private static void AttachNativeBackButton(NRunHistory view)
    {
        try
        {
            var submenuType = typeof(NRunHistory).BaseType;
            var backButton = submenuType is null
                ? null
                : AccessTools.Field(submenuType, "_backButton")?.GetValue(view) as NBackButton;
            if (backButton is null || !GodotObject.IsInstanceValid(backButton))
            {
                ModLog.Write("Memory panel could not find the native run-history back button.");
                return;
            }

            _backButton = backButton;
            // NBackButton._Ready() 会调用 OnDisable()：除了设置禁用状态，
            // 还会把按钮移动到 hide position。只设置 Visible 不会恢复位置，
            // 因此必须走原生 Enable()，让按钮回到 show position 并重新接受点击。
            _backButton.Enable();
            _backButton.Visible = true;
            ModLog.Write("Memory panel attached the native run-history back button.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Memory panel back-button setup failed: {exception}");
        }
    }

    internal static bool TryHandleNativeBackButton(NBackButton button)
    {
        if (Volatile.Read(ref _open) == 0 || !ReferenceEquals(button, _backButton))
            return false;

        ClosePanel();
        return true;
    }

    // 用某次死亡记录拼出原版历史记录需要的最小数据：作战记录 + 玩家（死亡卡组）。
    // 遗物、药水、徽章、种子等留空，原版 UI 显示为空区块；总时间仍按记忆
    // 保存的游玩时长显示，右上角日期使用本局开始时间 + 该次记录结束时长。
    private static RunHistory BuildMemoryHistory(int episodeIndex)
    {
        var livePlayer = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault()
            ?? throw new InvalidOperationException("No live player for the memory panel.");
        var episode = AmnesiaState.GetEpisode(episodeIndex);
        var deathPlaytime = episode?.DeathPlaytimeSeconds ?? AmnesiaState.DeathPlaytimeSeconds;
        var runStartTime = episode?.RunStartTime ?? 0;
        var memoryEndTime = runStartTime > 0
            ? runStartTime + deathPlaytime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var actIds = episode?.ActIds ?? AmnesiaState.GetHistoryActIds();
        var historyEntries = episode?.HistoryEntries ?? AmnesiaState.GetHistoryEntries();
        var deathDeck = episode is { DeathDeck.Count: > 0 }
            ? episode.DeathDeck
            : AmnesiaState.GetDeathDeck();
        var character = episode is not null && !string.IsNullOrEmpty(episode.CharacterId)
            ? EncounterJournalStore.TextToId(episode.CharacterId) ?? livePlayer.Character.Id
            : livePlayer.Character.Id;

        return new RunHistory
        {
            Acts = actIds
                .Select(id => EncounterJournalStore.TextToId(id) ?? ModelId.none)
                .ToList(),
            MapPointHistory = historyEntries,
            Players = new List<RunHistoryPlayer>
            {
                new RunHistoryPlayer
                {
                    Id = livePlayer.NetId,
                    Character = character,
                    Deck = deathDeck
                }
            },
            RunTime = deathPlaytime,
            // NRunHistory 把 RunHistory.StartTime 渲染为右上角的日期时间。
            // 这里传入该条记忆结束/死亡时刻，避免用“当前时间回推”造成
            // 记忆越新、显示日期反而越早的倒序现象。
            StartTime = memoryEndTime,
            Win = false
        };
    }
}

[HarmonyPatch(typeof(NClickableControl), "OnRelease")]
internal static class AmnesiaMemoryNativeBackButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NClickableControl __instance) =>
        __instance is not NBackButton backButton ||
        !AmnesiaMemoryPanel.TryHandleNativeBackButton(backButton);
}
