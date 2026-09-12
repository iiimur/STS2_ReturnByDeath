// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterNextAct))]
internal static class PrideFinalBossEnterNextActPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        if (!PrideFinalBossTransition.TryInterceptEnterNextAct(out var transitionTask))
            return true;

        __result = transitionTask;
        return false;
    }
}

// 怠惰线最终 Boss 胜利后不进入建筑师：先播放怠惰结局视频，随后直接调用
// 原版 OnEnded(true) + GameOverScreen，使用胜利状态完成终局结算但跳过建筑师。
internal static class PrideFinalBossTransition
{
    private const string VideoFileName = "傲慢结局继续.ogv";
    private const string AfterVideoAudioFileName = "傲慢结局视频之后.wav";
    private const string OpeningAudioFileName = "傲慢结局到此为止了.wav";
    private static CanvasLayer? _layer;
    private static VideoStreamPlayer? _video;
    private static Button? _skipButton;
    private static TaskCompletionSource? _videoFinished;
    private static int _started;
    private static int _allowNativeEnterNextAct;

    public static void ResetForNewRun()
    {
        Volatile.Write(ref _started, 0);
        Volatile.Write(ref _allowNativeEnterNextAct, 0);
        Cleanup();
    }

    public static bool TryInterceptEnterNextAct(out Task transitionTask)
    {
        transitionTask = Task.CompletedTask;
        if (Volatile.Read(ref _allowNativeEnterNextAct) != 0)
            return false;

        if (!RouteState.IsPrideRoute)
            return false;

        if (Volatile.Read(ref _started) != 0)
            return false;

        var runManager = RunManager.Instance;
        var state = runManager?.DebugOnlyGetState();
        if (state is null)
        {
            ModLog.Write("Pride final-boss transition skipped because run state is unavailable.");
            return false;
        }

        // 最终层从 Boss 结算进入建筑师时，房间节点有时已经切换为结算容器，
        // 因此这里以“第三层 + 傲慢线”的真实推进调用作为判定，不再读取易变的 UI 房间类型。
        if (state.CurrentActIndex != 2)
        {
            return false;
        }

        var videoPath = ModAssetPaths.Video(VideoFileName);
        if (!File.Exists(videoPath))
        {
            ModLog.Write($"Pride final-boss transition skipped because video is missing: {videoPath}");
            return false;
        }

        Volatile.Write(ref _started, 1);
        transitionTask = PlayThenEnterArchitectAsync();
        ModLog.Write("Pride final-boss transition intercepted native EnterNextAct before entering the Architect.");
        return true;
    }

    private static async Task PlayThenEnterArchitectAsync()
    {
        try
        {
            await PrideFinalBossCutin.PlayAsync(OpeningAudioFileName);
            PlayVideo();
            var completion = _videoFinished?.Task
                ?? throw new InvalidOperationException("Pride transition video completion task was not created.");

            // Finished 信号在 Godot 主线程发出；在同一线程恢复原版推进，
            // 避免把场景切换交给后台计时器。
            await completion;

            Cleanup();
            if (CosmeticAudio.TryPlay(AfterVideoAudioFileName))
                ModLog.Write($"Started pride final-boss after-video audio: {AfterVideoAudioFileName}.");

            var runManager = RunManager.Instance
                ?? throw new InvalidOperationException("RunManager is unavailable after pride transition video.");
            Volatile.Write(ref _allowNativeEnterNextAct, 1);
            try
            {
                await runManager.EnterNextAct();
                ModLog.Write("Pride final-boss transition completed; Architect event entered.");
            }
            finally
            {
                Volatile.Write(ref _allowNativeEnterNextAct, 0);
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Pride final-boss transition failed: {exception}");
            Cleanup();
            Volatile.Write(ref _allowNativeEnterNextAct, 1);
            try
            {
                if (RunManager.Instance is { } runManager)
                    await runManager.EnterNextAct();
            }
            finally
            {
                Volatile.Write(ref _allowNativeEnterNextAct, 0);
            }
        }
    }

    private static void PlayVideo()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable for pride transition video.");

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            _layer = new CanvasLayer
            {
                Name = "ReturnByDeathPrideFinalBossTransition",
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

            var videoPath = ModAssetPaths.Video(VideoFileName);
            _video = new VideoStreamPlayer
            {
                Stream = new VideoStreamTheora { File = videoPath },
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

            // 与奥托事件一致：右上角可跳过。此处不保留最后一帧，点击后直接视为
            // 视频播放完毕，立刻执行“视频后音效 + 进入建筑师”的后续流程。
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
            ModLog.Write($"Started pride final-boss transition video: {VideoFileName}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Pride final-boss transition video setup failed: {exception}");
            _videoFinished?.TrySetResult();
            throw;
        }
    }

    private static void OnVideoFinished() => _videoFinished?.TrySetResult();

    private static void OnSkipPressed()
    {
        if (_videoFinished is null || _videoFinished.Task.IsCompleted)
            return;

        if (_video is not null && GodotObject.IsInstanceValid(_video))
            _video.Stop();

        ModLog.Write("Pride final-boss transition video skipped; continuing to Architect.");
        _videoFinished.TrySetResult();
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

// 傲慢线最终 Boss 结算后、视频前的短演出。它运行在被拦截的 EnterNextAct
// Task 中，所以此阶段不会进入建筑师，也不会让原版结算流程提前切场。
internal static class PrideFinalBossCutin
{
    private const string Cutin1FileName = "cutin-1.png";
    private const string Cutin2FileName = "cutin-2.png";
    private const string Cutin3FileName = "cutin-3.png";
    private const string Glass1FileName = "glass-1.png";
    private const string Glass2FileName = "glass-2.png";
    private const string Glass3FileName = "glass-3.png";
    private const string EmiliaFileName = "傲慢结局-爱蜜莉雅.png";
    private const double CutinInitialHoldSeconds = 1d;
    private const double CutinFinalHoldSeconds = 1.5d;
    private const double CutinSwitchSeconds = 0.02d;
    private const double GlassSwitchSeconds = 0.02d;
    private const double EmiliaMoveSeconds = 0.8d;

    public static async Task PlayAsync(string audioFileName)
    {
        CanvasLayer? layer = null;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable for pride opening cutin.");

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            var cutin1 = LoadTexture(Cutin1FileName);
            var cutin2 = LoadTexture(Cutin2FileName);
            var cutin3 = LoadTexture(Cutin3FileName);
            var glass1 = LoadTexture(Glass1FileName);
            var glass2 = LoadTexture(Glass2FileName);
            var glass3 = LoadTexture(Glass3FileName);
            var emilia = LoadTexture(EmiliaFileName);

            layer = new CanvasLayer
            {
                Name = "ReturnByDeathPrideFinalBossOpening",
                Layer = 10001,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(layer);
            layer.AddChild(new ColorRect
            {
                // 动画 1/2/3 都是透明抠图，只压暗原场景，不使用全黑背景。
                // 40% 黑色遮罩对应约 60% 的背景亮度。
                Color = new Color(0f, 0f, 0f, 0.4f),
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ProcessMode = Node.ProcessModeEnum.Always
            });

            var cutin = new Sprite2D
            {
                Texture = cutin1,
                Centered = true,
                Position = viewportSize / 2f,
                Scale = FitScale(cutin1, viewportSize),
                ZIndex = 1,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            layer.AddChild(cutin);

            var emiliaSprite = new Sprite2D
            {
                Texture = emilia,
                Centered = true,
                Position = new Vector2(viewportSize.X * 0.59f, viewportSize.Y / 2f),
                Scale = FitScale(emilia, viewportSize, 1f),
                ZIndex = 2,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            layer.AddChild(emiliaSprite);

            // 音效在两段画面演出开始时播放；后续视频结束音效会按原流程另行播放。
            CosmeticAudio.TryPlay(audioFileName);

            var cutinTask = AnimateCutinAsync(tree, cutin, cutin2, cutin3);
            var emiliaTask = AnimateEmiliaAsync(emiliaSprite, viewportSize);
            var glassTask = AnimateGlassAsync(tree, layer, viewportSize, glass1, glass2, glass3);
            await Task.WhenAll(cutinTask, emiliaTask, glassTask);
            ModLog.Write("Pride final-boss opening cutin completed; starting the transition video.");
        }
        catch (Exception exception)
        {
            // 新增演出不能阻断原本的最终 Boss 流程；资源异常时直接继续视频。
            ModLog.Write($"Pride final-boss opening cutin failed: {exception.Message}");
        }
        finally
        {
            if (layer is not null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
        }
    }

    private static async Task AnimateCutinAsync(SceneTree tree, Sprite2D cutin, Texture2D cutin2, Texture2D cutin3)
    {
        await WaitAsync(tree, CutinInitialHoldSeconds);
        if (!GodotObject.IsInstanceValid(cutin))
            return;

        cutin.Texture = cutin2;
        await WaitAsync(tree, CutinSwitchSeconds);
        if (!GodotObject.IsInstanceValid(cutin))
            return;

        cutin.Texture = cutin3;
        await WaitAsync(tree, CutinFinalHoldSeconds);
    }

    private static async Task AnimateEmiliaAsync(Sprite2D emilia, Vector2 viewportSize)
    {
        var target = new Vector2(viewportSize.X * 0.41f, viewportSize.Y / 2f);
        var tween = emilia.CreateTween();
        tween.SetProcessMode(Tween.TweenProcessMode.Idle);
        tween.SetIgnoreTimeScale(true);
        tween.TweenProperty(emilia, new NodePath("position"), target, EmiliaMoveSeconds)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.InOut);
        await emilia.ToSignal(tween, Tween.SignalName.Finished);
    }

    private static async Task AnimateGlassAsync(
        SceneTree tree,
        CanvasLayer layer,
        Vector2 viewportSize,
        Texture2D glass1,
        Texture2D glass2,
        Texture2D glass3)
    {
        await WaitAsync(tree, CutinInitialHoldSeconds);
        if (!GodotObject.IsInstanceValid(layer))
            return;

        var glass = new Sprite2D
        {
            Texture = glass1,
            Centered = true,
            Position = viewportSize / 2f,
            Scale = FitScale(glass1, viewportSize),
            ZIndex = 3,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        layer.AddChild(glass);

        await WaitAsync(tree, GlassSwitchSeconds);
        if (!GodotObject.IsInstanceValid(glass))
            return;

        glass.Texture = glass2;
        await WaitAsync(tree, GlassSwitchSeconds);
        if (!GodotObject.IsInstanceValid(glass))
            return;

        glass.Texture = glass3;
        ModLog.Write("Pride final-boss glass animation reached its final frame.");
    }

    private static async Task WaitAsync(SceneTree tree, double seconds)
    {
        var timer = tree.CreateTimer(seconds, true, false, true);
        await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitSignal(
            timer,
            SceneTreeTimer.SignalName.Timeout,
            tree.Root);
    }

    private static Texture2D LoadTexture(string fileName)
    {
        var path = ModAssetPaths.Image(fileName);
        var image = Image.LoadFromFile(path);
        if (image is null)
            throw new FileNotFoundException($"Pride opening image could not be loaded: {path}", path);
        return ImageTexture.CreateFromImage(image);
    }

    private static Vector2 FitScale(Texture2D texture, Vector2 viewportSize, float factor = 0.96f)
    {
        var scale = MathF.Min(
            viewportSize.X / texture.GetWidth(),
            viewportSize.Y / texture.GetHeight());
        return new Vector2(scale * factor, scale * factor);
    }
}

// BeforeEventStarted 是先古事件开始前的原版生命周期方法。
// 原版在这个调用链中使用 CreatureCmd.Heal 给玩家回血；用一个短暂的
// 上下文标记，只让这一次 Heal 被改写，其他回血来源保持原样。
