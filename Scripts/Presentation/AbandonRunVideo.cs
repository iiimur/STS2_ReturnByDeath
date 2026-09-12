// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnYesButtonPressed")]
internal static class AbandonRunVideoPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NAbandonRunConfirmPopup __instance)
    {
        // 代理致死预警复用同一原版弹窗，但不是“放弃”操作；交给专用补丁
        // 消费按钮，不能误触发奥托/放弃视频。
        if (ProxyDeathWarning.IsPopupActive)
            return true;

        if (!AbandonRunVideo.TryBegin(out var checkpoint))
            return true;

        __instance.QueueFree();
        AbandonRunVideo.Play(checkpoint);
        return false;
    }
}

internal static class AbandonRunVideo
{
    private const int MinimumGuilty = 3;
    private const string VideoFileName = "奥托战神-剪.ogv";
    private const string RejectVideoFileName = "拒绝奥托.ogv";
    private const double VideoLastFramePosition = 16.97d;
    private const string EventUsedMarkerPrefix = "return-by-death-otto-event-used-v1:";
    private static readonly string EventUsedMarkerPath = Path.Combine(
        ModLog.ModDirectory,
        "return-by-death.otto-event-used");
    private static CanvasLayer? _layer;
    private static VideoStreamPlayer? _video;
    private static ColorRect? _choiceDim;
    private static Button? _skipButton;
    private static TextureButton? _acceptButton;
    private static TextureButton? _rejectButton;
    private static SerializableRun? _checkpoint;
    private static int _started;
    private static int _selected;

    public static void ResetForNewRun()
    {
        try { File.Delete(EventUsedMarkerPath); } catch { }
        OttoPendingCleanup.Clear();
        OttoAcceptanceState.Clear();
    }

    public static bool TryBegin(out SerializableRun checkpoint)
    {
        checkpoint = null!;
        if (RouteState.IsIfRoute)
        {
            ModLog.Write("Otto event skipped because the run is on an IF route.");
            return false;
        }

        if (Volatile.Read(ref _started) != 0)
            return false;

        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 1)
        {
            ModLog.Write($"Otto event skipped outside act 2: act={state?.CurrentActIndex.ToString() ?? "unknown"}.");
            return false;
        }

        if (!state.Players.Any(HasEnoughGuilty))
            return false;

        if (!CheckpointStore.TryLoad(out checkpoint))
        {
            ModLog.Write("Abandon video skipped because no recovery checkpoint was available.");
            return false;
        }

        if (WasUsedForRun(checkpoint))
            return false;

        if (!File.Exists(ModAssetPaths.Video(VideoFileName)))
        {
            ModLog.Write($"Abandon video skipped because the converted video is missing: {VideoFileName}");
            return false;
        }

        if (!File.Exists(ModAssetPaths.Video(RejectVideoFileName)))
        {
            ModLog.Write($"Abandon video skipped because the converted reject video is missing: {RejectVideoFileName}");
            return false;
        }

        Volatile.Write(ref _started, 1);
        _checkpoint = checkpoint;
        return true;
    }

    public static void Play(SerializableRun checkpoint)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable.");

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            _layer = new CanvasLayer
            {
                Name = "ReturnByDeathAbandonVideo",
                Layer = 10000,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(_layer);

            var black = new ColorRect
            {
                Color = Colors.Black,
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 0
            };
            _layer.AddChild(black);

            var videoPath = ModAssetPaths.Video(VideoFileName);
            var stream = new VideoStreamTheora { File = videoPath };
            _video = new VideoStreamPlayer
            {
                Stream = stream,
                Expand = true,
                Loop = false,
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 1,
                ProcessMode = Node.ProcessModeEnum.Always
            };
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

            // 视频结束后压暗背景，突出选项按钮；按钮本身位于更高层级，不会被遮罩影响。
            _choiceDim = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.5f),
                Position = Vector2.Zero,
                Size = viewportSize,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 2,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            _layer.AddChild(_choiceDim);

            var acceptNormal = LoadButtonTexture("接受奥托.png");
            var acceptHover = LoadButtonTexture("接受奥托-悬浮.png");
            var rejectNormal = LoadButtonTexture("拒绝奥托.png");
            var rejectHover = LoadButtonTexture("拒绝奥托-悬浮.png");
            var buttonSize = new Vector2(650f, 110f);
            var buttonX = Math.Max(0f, (viewportSize.X - buttonSize.X) / 2f);
            var buttonGap = 18f;
            var firstButtonY = Math.Max(0f, (viewportSize.Y - buttonSize.Y * 2f - buttonGap) / 2f);

            _acceptButton = new TextureButton
            {
                TextureNormal = acceptNormal,
                TextureHover = acceptHover,
                TexturePressed = acceptHover,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.Scale,
                Position = new Vector2(buttonX, firstButtonY),
                Size = buttonSize,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 3,
                ProcessMode = Node.ProcessModeEnum.Always,
                FocusMode = Control.FocusModeEnum.All
            };
            _acceptButton.Pressed += OnAcceptPressed;
            _layer.AddChild(_acceptButton);

            _rejectButton = new TextureButton
            {
                TextureNormal = rejectNormal,
                TextureHover = rejectHover,
                TexturePressed = rejectHover,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.Scale,
                Position = new Vector2(buttonX, firstButtonY + buttonSize.Y + buttonGap),
                Size = buttonSize,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 3,
                ProcessMode = Node.ProcessModeEnum.Always,
                FocusMode = Control.FocusModeEnum.All
            };
            _rejectButton.Pressed += OnRejectPressed;
            _layer.AddChild(_rejectButton);

            // 延后一帧播放，让黑屏覆盖层先建立，避免视频首帧前露出原界面。
            _video.CallDeferred("play");
            ModLog.Write($"Started abandon video with {CountGuilty(RunManager.Instance?.DebugOnlyGetState())} Guilty cards.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Abandon video setup failed: {exception}");
            Cleanup();
            Volatile.Write(ref _started, 0);
            _checkpoint = null;
        }
    }

    private static void OnVideoFinished()
    {
        if (_acceptButton is null || !GodotObject.IsInstanceValid(_acceptButton) ||
            _rejectButton is null || !GodotObject.IsInstanceValid(_rejectButton))
            return;

        if (_skipButton is not null && GodotObject.IsInstanceValid(_skipButton))
        {
            _skipButton.Visible = false;
            _skipButton.Disabled = true;
        }

        _acceptButton.Visible = true;
        _rejectButton.Visible = true;
        if (_choiceDim is not null && GodotObject.IsInstanceValid(_choiceDim))
            _choiceDim.Visible = true;
        _acceptButton.GrabFocus();
        ModLog.Write("Abandon video finished; Otto choice buttons shown.");
    }

    private static void OnSkipPressed()
    {
        if (Volatile.Read(ref _selected) != 0 || _video is null || !GodotObject.IsInstanceValid(_video))
            return;

        // 停在接近最后一帧的位置，保留“视频结尾画面”作为分支选择的背景。
        _video.StreamPosition = VideoLastFramePosition;
        _video.Paused = true;
        ModLog.Write("Otto abandon video skipped to its final frame.");
        OnVideoFinished();
    }

    private static Texture2D LoadButtonTexture(string fileName)
    {
        var path = ModAssetPaths.Image(fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Otto choice button texture is missing: {path}", path);

        var image = Image.LoadFromFile(path);
        if (image is null)
            throw new InvalidOperationException($"Could not load Otto choice button texture: {path}");

        return ImageTexture.CreateFromImage(image);
    }

    private static void OnAcceptPressed()
    {
        if (Interlocked.Exchange(ref _selected, 1) != 0 || _checkpoint is null)
            return;

        MarkUsedForRun(_checkpoint);
        _acceptButton!.Disabled = true;
        _rejectButton!.Disabled = true;
        _ = AcceptOttoAsync();
    }

    private static void OnRejectPressed()
    {
        if (Interlocked.Exchange(ref _selected, 1) != 0 || _checkpoint is null)
            return;

        MarkUsedForRun(_checkpoint);
        _acceptButton!.Disabled = true;
        _rejectButton!.Disabled = true;
        _acceptButton.Visible = false;
        _rejectButton.Visible = false;
        StartRejectVideo();
    }

    private static void StartRejectVideo()
    {
        if (_video is null || !GodotObject.IsInstanceValid(_video))
        {
            ModLog.Write("Reject Otto video could not start because the video player is unavailable.");
            BeginRecoveryAfterRejectVideo();
            return;
        }

        try
        {
            _video.Finished -= OnVideoFinished;
            _video.Finished -= OnRejectVideoFinished;
            _video.Finished += OnRejectVideoFinished;
            _video.Paused = false;
            _video.StreamPosition = 0d;
            _video.Stream = new VideoStreamTheora
            {
                File = ModAssetPaths.Video(RejectVideoFileName)
            };
            if (_choiceDim is not null && GodotObject.IsInstanceValid(_choiceDim))
                _choiceDim.Visible = false;

            _video.CallDeferred("play");
            ModLog.Write($"Started reject Otto video: {RejectVideoFileName}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reject Otto video setup failed: {exception}");
            BeginRecoveryAfterRejectVideo();
        }
    }

    private static void OnRejectVideoFinished()
    {
        ModLog.Write("Reject Otto video finished; starting recovery automatically.");
        BeginRecoveryAfterRejectVideo();
    }

    private static void BeginRecoveryAfterRejectVideo()
    {
        if (_checkpoint is null)
            return;

        var checkpoint = _checkpoint;
        // 放弃后走向回归同样会退回检查点，先记录当前时间线的探索足迹。
        if (RunManager.Instance?.DebugOnlyGetState() is { } abandonState)
            ExploredNodesMemory.Record(abandonState);
        RecoveryMarker.Mark();
        if (!RecoveryFlow.TryStart())
        {
            RecoveryMarker.ReleaseForRetry();
            ModLog.Write("Recovery could not start after rejecting Otto.");
            return;
        }

        _ = ContinueRecoveryAsync(checkpoint);
    }

    private static async Task AcceptOttoAsync()
    {
        var saveCompleted = false;
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState()
                ?? throw new InvalidOperationException("RunState is unavailable for Otto acceptance.");

            // 接受奥托后只设置跨重载授权；奥托本身是战斗临时卡，不进入牌组。
            OttoCardLifecycle.MarkAccepted();

            // 先标记待清理，再保存当前运行。删牌故意延迟到 LoadRun 完成后，
            // 因为当前界面仍是设置页，在这里执行回血/删牌会导致动画丢失或界面状态卡住。
            OttoPendingCleanup.Mark();
            OttoAcceptanceState.Mark();
            await SaveManager.Instance.SaveRun(state.CurrentRoom, true);
            saveCompleted = true;

            var loadResult = SaveManager.Instance.LoadRunSave();
            if (!loadResult.Success || loadResult.SaveData is null)
                throw new InvalidOperationException($"Could not read the saved run for Otto reload: {loadResult.ErrorMessage}");

            var savedRun = loadResult.SaveData;
            Cleanup();
            _checkpoint = null;
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _selected, 0);

            // 从这里开始直到重新载入结束，拦截的仅是运行 BGM；音效与环境音仍走原版。
            OttoAcceptanceMusic.Arm();
            RewardSnapshotStore.Clear();
            RunManager.Instance.CleanUp(false);
            var reloadedState = RunState.FromSerializable(savedRun);
            await RunManager.Instance.SetUpSavedSingleplayer(reloadedState, savedRun);
            var game = NGame.Instance
                ?? throw new InvalidOperationException("NGame is unavailable during Otto reload.");
            await game.LoadRun(reloadedState, savedRun.PreFinishedRoom);
            ModLog.Write("Accepted Otto: healed to max HP, saved the run, and reloaded it for deferred wound cleanup.");
        }
        catch (Exception exception)
        {
            if (!saveCompleted)
                OttoPendingCleanup.Clear();
            OttoAcceptanceMusic.CancelAndRestoreNativeMusic();
            ModLog.Write($"Accept Otto failed: {exception}");
        }
        finally
        {
            Cleanup();
            _checkpoint = null;
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _selected, 0);
        }
    }

    private static async Task ContinueRecoveryAsync(SerializableRun checkpoint)
    {
        try
        {
            await RecoveryFlow.RestoreCheckpointAsync(checkpoint, playRecoveryAudio: true);
        }
        finally
        {
            Cleanup();
            _checkpoint = null;
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _selected, 0);
        }
    }

    private static bool WasUsedForRun(SerializableRun checkpoint)
    {
        try
        {
            return string.Equals(
                File.ReadAllText(EventUsedMarkerPath),
                EventUsedMarkerPrefix + GetRunKey(checkpoint),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void MarkUsedForRun(SerializableRun checkpoint)
    {
        try
        {
            File.WriteAllText(EventUsedMarkerPath, EventUsedMarkerPrefix + GetRunKey(checkpoint));
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not persist Otto event marker: {exception.Message}");
        }
    }

    private static string GetRunKey(SerializableRun checkpoint) => CheckpointStore.GetRunKey(checkpoint);

    private static bool HasEnoughGuilty(Player player) => CountGuilty(player) >= MinimumGuilty;

    private static int CountGuilty(Player? player) =>
        player?.Deck.Cards.Count(card => card is Guilty) ?? 0;

    private static int CountGuilty(RunState? state) =>
        state?.Players.Sum(CountGuilty) ?? 0;

    private static void Cleanup()
    {
        // 视频节点随后会随顶层 CanvasLayer 一起 QueueFree；不要在这里
        // 重复解除 Finished 信号，否则 Godot 会对已断开的连接报错。
        _choiceDim = null;
        if (_skipButton is not null && GodotObject.IsInstanceValid(_skipButton))
            _skipButton.Pressed -= OnSkipPressed;
        if (_acceptButton is not null && GodotObject.IsInstanceValid(_acceptButton))
            _acceptButton.Pressed -= OnAcceptPressed;
        if (_rejectButton is not null && GodotObject.IsInstanceValid(_rejectButton))
            _rejectButton.Pressed -= OnRejectPressed;
        if (_layer is not null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _video = null;
        _skipButton = null;
        _acceptButton = null;
        _rejectButton = null;
        _layer = null;
    }
}
