// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
internal static class SlothFinalBossEnterNextActPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        if (!SlothFinalBossTransition.TryInterceptEnterNextAct(out var transitionTask))
            return true;

        __result = transitionTask;
        return false;
    }
}

internal static class SlothFinalBossTransition
{
    private const string VideoFileName = "怠惰结局.ogv";
    private const string DeathAudioFileName = "怠惰结局死亡用.wav";
    private static CanvasLayer? _layer;
    private static VideoStreamPlayer? _video;
    private static Button? _skipButton;
    private static TaskCompletionSource? _videoFinished;
    private static int _started;
    private static int _deathSettlementActive;
    private static int _summaryWatcherActive;

    public static bool IsDeathSettlementActive => Volatile.Read(ref _deathSettlementActive) != 0;

    public static void ApplyVictorySummaryText(NGameOverScreen screen)
    {
        if (!IsDeathSettlementActive || screen is null || !GodotObject.IsInstanceValid(screen))
            return;

        try
        {
            var victoryDamage = AccessTools.Field(typeof(NGameOverScreen), "_victoryDamageLabel")?
                .GetValue(screen) as MegaRichTextLabel;
            if (victoryDamage is not null && GodotObject.IsInstanceValid(victoryDamage))
            {
                const string blue = "89c4e1";
                victoryDamage.Text =
                    "你没有与建筑师进行作战……\n" +
                    $"在遥远的卡拉拉基，你却与[color=#{blue}]那个始终相信着你的人[/color]，一同找到了另一种答案。\n" +
                    $"即便尖塔依然高耸、建筑师屹立不倒，你也终于拥有了一个无需死亡回归也值得继续活下去的[color=#{blue}]明天[/color]。";
            }

            var deathQuote = AccessTools.Field(typeof(NGameOverScreen), "_deathQuote")?
                .GetValue(screen) as MegaRichTextLabel;
            if (deathQuote is not null && GodotObject.IsInstanceValid(deathQuote))
                deathQuote.Text = string.Empty;

            ModLog.Write("Applied Sloth victory summary text and blue emphasized quote text.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth victory summary text override failed: {exception}");
        }
    }

    public static void ResetForNewRun()
    {
        Volatile.Write(ref _started, 0);
        Volatile.Write(ref _deathSettlementActive, 0);
        Volatile.Write(ref _summaryWatcherActive, 0);
        Cleanup();
    }

    public static void StartVictorySummaryWatcher(NGameOverScreen screen)
    {
        if (!IsDeathSettlementActive ||
            Interlocked.CompareExchange(ref _summaryWatcherActive, 1, 0) != 0)
            return;

        _ = WatchVictorySummaryAsync(screen);
    }

    private static async Task WatchVictorySummaryAsync(NGameOverScreen screen)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;

            for (var frame = 0; frame < 150 && IsDeathSettlementActive &&
                 GodotObject.IsInstanceValid(screen); frame++)
            {
                ApplyAnimatedVictorySummaryText(screen, frame);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth victory summary watcher failed: {exception}");
        }
        finally
        {
            Volatile.Write(ref _summaryWatcherActive, 0);
        }
    }

    private static void ApplyAnimatedVictorySummaryText(NGameOverScreen screen, int frame)
    {
        var victoryLines = new[]
        {
            "你没有与建筑师进行作战……",
            $"在遥远的卡拉拉基，你却与[color=#89c4e1]那个始终相信着你的人[/color]，一同找到了另一种答案。",
            $"即便尖塔依然高耸、建筑师屹立不倒，你也终于拥有了一个无需死亡回归也值得继续活下去的[color=#89c4e1]明天[/color]。"
        };

        var victoryCount = Math.Min(victoryLines.Length, frame / 8 + 1);
        var victoryDamage = AccessTools.Field(typeof(NGameOverScreen), "_victoryDamageLabel")?
            .GetValue(screen) as MegaRichTextLabel;
        var deathQuote = AccessTools.Field(typeof(NGameOverScreen), "_deathQuote")?
            .GetValue(screen) as MegaRichTextLabel;
        if (victoryDamage is not null && GodotObject.IsInstanceValid(victoryDamage))
            victoryDamage.Text = string.Join("\n", victoryLines.Take(victoryCount));
        if (deathQuote is not null && GodotObject.IsInstanceValid(deathQuote))
            deathQuote.Text = string.Empty;
    }

    public static bool TryInterceptEnterNextAct(out Task transitionTask)
    {
        transitionTask = Task.CompletedTask;
        if (!RouteState.IsSlothRoute || Volatile.Read(ref _started) != 0)
            return false;

        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 2)
            return false;

        var videoPath = ModAssetPaths.Video(VideoFileName);
        if (!File.Exists(videoPath))
        {
            ModLog.Write($"Sloth finale skipped because video is missing: {videoPath}");
            return false;
        }

        Volatile.Write(ref _started, 1);
        IfAchievements.Unlock("sloth_ending");
        transitionTask = PlayThenSettleAsync();
        ModLog.Write("Sloth final-boss transition intercepted native EnterNextAct.");
        return true;
    }

    private static async Task PlayThenSettleAsync()
    {
        try
        {
            PlayVideo();
            var completion = _videoFinished?.Task
                ?? throw new InvalidOperationException("Sloth finale video completion was not created.");
            await completion;
            Cleanup();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth finale video failed; continuing to death settlement: {exception}");
            Cleanup();
        }

        await EnterDeathSettlementAsync();
    }

    private static async Task EnterDeathSettlementAsync()
    {
        try
        {
            Volatile.Write(ref _deathSettlementActive, 1);
            if (!CosmeticAudio.TryPlay(DeathAudioFileName))
                ModLog.Write($"Sloth finale death WAV could not start: {DeathAudioFileName}");

            var runManager = RunManager.Instance
                ?? throw new InvalidOperationException("RunManager is unavailable for the Sloth finale.");
            // 原版建筑师的胜利分支最终调用 RunManager.WinRun；它负责完整的
            // 胜利时间、存档、结算页与角色收尾状态。这里只跳过建筑师事件本身。
            var winRun = AccessTools.Method(typeof(RunManager), "WinRun", Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(RunManager).FullName, "WinRun");
            if (winRun.Invoke(runManager, null) is not Task winTask)
                throw new InvalidOperationException("Native WinRun did not return a Task.");
            await winTask;
            ModLog.Write("Sloth finale entered native victory settlement after the ending video.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth finale death settlement failed: {exception}");
        }
        finally
        {
            // 保持此标记到新开局，确保 game-over 音频和 OnEnded 补丁不会把结局
            // 误判成普通死亡回归；NewRunPatch 会重置它。
            await Task.Yield();
        }
    }

    private static void PlayVideo()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root is null)
            throw new InvalidOperationException("SceneTree root is unavailable for the Sloth finale video.");

        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        _layer = new CanvasLayer
        {
            Name = "ReturnByDeathSlothFinale",
            Layer = 10000,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        tree.Root.AddChild(_layer);
        _layer.AddChild(new ColorRect
        {
            Color = Colors.Black,
            Position = Vector2.Zero,
            Size = viewportSize,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ProcessMode = Node.ProcessModeEnum.Always
        });

        _video = new VideoStreamPlayer
        {
            Stream = new VideoStreamTheora { File = ModAssetPaths.Video(VideoFileName) },
            Expand = true,
            Loop = false,
            Position = Vector2.Zero,
            Size = viewportSize,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        _videoFinished = new TaskCompletionSource();
        _video.Finished += OnVideoFinished;
        _layer.AddChild(_video);

        _skipButton = new Button
        {
            Text = "跳过",
            Position = new Vector2(Math.Max(0f, viewportSize.X - 156f), 26f),
            Size = new Vector2(130f, 54f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 4,
            ProcessMode = Node.ProcessModeEnum.Always,
            FocusMode = Control.FocusModeEnum.All
        };
        _skipButton.Pressed += OnSkipPressed;
        _layer.AddChild(_skipButton);
        _video.CallDeferred("play");
        ModLog.Write($"Started Sloth finale video: {VideoFileName}.");
    }

    private static void OnVideoFinished() => _videoFinished?.TrySetResult();

    private static void OnSkipPressed()
    {
        if (_videoFinished is null || _videoFinished.Task.IsCompleted)
            return;

        _video?.Stop();
        _videoFinished.TrySetResult();
        ModLog.Write("Sloth finale video skipped; continuing to death settlement.");
    }

    private static void Cleanup()
    {
        if (_video is not null && GodotObject.IsInstanceValid(_video))
            _video.Finished -= OnVideoFinished;
        if (_skipButton is not null && GodotObject.IsInstanceValid(_skipButton))
            _skipButton.Pressed -= OnSkipPressed;
        if (_layer is not null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _video = null;
        _skipButton = null;
        _layer = null;
        _videoFinished = null;
    }
}

[HarmonyPatch(typeof(NGameOverScreen), "OpenSummaryScreen")]
internal static class SlothVictorySummaryTextPatch
{
    [HarmonyPostfix]
    private static void Postfix(NGameOverScreen __instance)
    {
        if (!SlothFinalBossTransition.IsDeathSettlementActive)
            return;

        SlothFinalBossTransition.StartVictorySummaryWatcher(__instance);
        // 原版 OpenSummaryScreen 会在同一帧继续挂载结算控件；延后一帧覆盖，
        // 避免被原版初始化逻辑写回。
        _ = ApplyNextFrameAsync(__instance);
    }

    private static async Task ApplyNextFrameAsync(NGameOverScreen screen)
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            SlothFinalBossTransition.ApplyVictorySummaryText(screen);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth summary text delayed apply failed: {exception}");
        }
    }
}

[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
internal static class SlothVictorySummaryReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NGameOverScreen __instance)
    {
        if (SlothFinalBossTransition.IsDeathSettlementActive)
            SlothFinalBossTransition.StartVictorySummaryWatcher(__instance);
    }
}

// NGameOverScreen 会在 AnimateRunSummary 中再次填充建筑师统计和引言；在该
// 原版动画任务完成后再覆盖一次，确保最终可见文本不会被写回。
[HarmonyPatch(typeof(NGameOverScreen), "AnimateRunSummary")]
internal static class SlothVictorySummaryAfterAnimationPatch
{
    [HarmonyPostfix]
    private static void Postfix(NGameOverScreen __instance, ref Task __result)
    {
        if (!SlothFinalBossTransition.IsDeathSettlementActive)
            return;

        var nativeAnimation = __result;
        __result = ApplyAfterAnimationAsync(nativeAnimation, __instance);
    }

    private static async Task ApplyAfterAnimationAsync(Task nativeAnimation, NGameOverScreen screen)
    {
        await nativeAnimation;
        SlothFinalBossTransition.ApplyVictorySummaryText(screen);
    }
}
