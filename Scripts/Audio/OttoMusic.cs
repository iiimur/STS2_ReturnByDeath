// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 接受奥托后的专属 BGM。它只接管 NRunMusicController 的背景音乐更新；
// 音效和环境音仍由原版音频系统处理。状态仅保存在进程内，因此退出游戏后
// 不会把专属 BGM 写入存档，下次进入将自然恢复原版音乐。
internal static class OttoAcceptanceMusic
{
    private const string MusicFileName = "接受奥托.ogg";
    // 素材更换前的旧时长，仅在音频长度读取失败时兜底。
    private const float FallbackMusicDurationSeconds = 248.615f;
    private static float _musicLengthSeconds = FallbackMusicDurationSeconds;
    private const float FadeDurationSeconds = 5f;
    private const float PostCombatVolume = 0.4f;
    private const float PostCombatHoldSeconds = 15f;
    private static AudioStreamPlayer? _player;
    private static Tween? _fadeTween;
    private static Tween? _tailFadeTween;
    private static int _pending;
    private static int _active;
    private static int _fading;
    private static int _nativeMusicRestoreQueued;

    public static bool IsSuppressingNativeRunMusic =>
        Volatile.Read(ref _pending) != 0 || Volatile.Read(ref _active) != 0;

    public static void Arm()
    {
        Volatile.Write(ref _pending, 1);
        ModLog.Write("Otto acceptance BGM armed for the upcoming reload.");
    }

    public static async Task StartAfterLoadAsync(Task loadTask)
    {
        try
        {
            await loadTask;
            if (Volatile.Read(ref _pending) == 0 || Volatile.Read(ref _active) != 0)
                return;

            // 让重新载入后的地图、事件或战斗节点先挂载完毕，再开始专属音乐。
            await Task.Yield();
            Start();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Otto acceptance BGM could not start after reload: {exception}");
            CancelAndRestoreNativeMusic();
        }
    }

    // 在 NRunMusicController.UpdateMusic 的 Prefix 中调用。手动更新环境音，
    // 但跳过原版背景音乐选择/播放，避免拦截短音效或环境音。
    public static bool TrySuppressNativeRunMusic(NRunMusicController controller)
    {
        if (!IsSuppressingNativeRunMusic)
            return false;

        controller.UpdateAmbience();
        return true;
    }

    public static void BeginFadeOut(string reason)
    {
        if (Volatile.Read(ref _active) == 0 ||
            Interlocked.Exchange(ref _fading, 1) != 0)
        {
            return;
        }

        if (_player is null || !GodotObject.IsInstanceValid(_player))
        {
            CancelAndRestoreNativeMusic();
            return;
        }

        // 用 Godot 原生 Tween 驱动音量，确保淡出由游戏主线程连续执行。
        // 不能依赖自定义 Node._Process：该节点在部分运行场景不会收到逐帧回调。
        _fadeTween?.Kill();
        _fadeTween = _player.CreateTween();
        _fadeTween.SetProcessMode(Tween.TweenProcessMode.Idle);
        _fadeTween.SetIgnoreTimeScale(true);
        _fadeTween.TweenProperty(
                _player,
                new NodePath("volume_linear"),
                0f,
                FadeDurationSeconds)
            .SetTrans(Tween.TransitionType.Linear)
            .SetEase(Tween.EaseType.In);
        _fadeTween.Finished += OnFadeTweenFinished;
        ModLog.Write($"Otto acceptance BGM fading out over {FadeDurationSeconds:0.#} seconds: {reason}.");
    }

    public static void BeginAfterFirstCombat(string reason)
    {
        if (Volatile.Read(ref _active) == 0 ||
            Interlocked.Exchange(ref _fading, 1) != 0)
        {
            return;
        }

        if (_player is null || !GodotObject.IsInstanceValid(_player))
        {
            CancelAndRestoreNativeMusic();
            return;
        }

        // 第一次战斗结束时，先在 5 秒内把专属 BGM 降到 20%；
        // 保持 20% 播放 15 秒后，再用原来的 5 秒淡出到静音。
        _tailFadeTween?.Kill();
        _tailFadeTween = null;
        _fadeTween?.Kill();
        _fadeTween = _player.CreateTween();
        _fadeTween.SetProcessMode(Tween.TweenProcessMode.Idle);
        _fadeTween.SetIgnoreTimeScale(true);
        _fadeTween.TweenProperty(
                _player,
                new NodePath("volume_linear"),
                PostCombatVolume,
                FadeDurationSeconds)
            .SetTrans(Tween.TransitionType.Linear)
            .SetEase(Tween.EaseType.In);
        _fadeTween.TweenInterval(PostCombatHoldSeconds);
        _fadeTween.TweenProperty(
                _player,
                new NodePath("volume_linear"),
                0f,
                FadeDurationSeconds)
            .SetTrans(Tween.TransitionType.Linear)
            .SetEase(Tween.EaseType.In);
        _fadeTween.Finished += OnFadeTweenFinished;
        ModLog.Write($"Otto acceptance BGM first-combat sequence started: " +
            $"fade to {PostCombatVolume:P0} over {FadeDurationSeconds:0.#} seconds, " +
            $"hold for {PostCombatHoldSeconds:0.#} seconds, then fade out.");
    }

    public static void CancelAndRestoreNativeMusic()
    {
        StopSpecialMusic();
        RestoreNativeMusic();
    }

    public static void StopImmediately()
    {
        StopSpecialMusic();
        // 此方法用于退出到主菜单或退出游戏。原版之后会自行启动相应场景的 BGM，
        // 因此不在这里主动重启当前运行音乐。
    }

    private static void Start()
    {
        try
        {
            var path = ModAssetPaths.Audio(MusicFileName);
            var stream = AudioStreamOggVorbis.LoadFromFile(path);
            if (stream is null)
                throw new InvalidOperationException($"Could not load Otto acceptance BGM: {path}");

            var streamLength = (float)stream.GetLength();
            if (streamLength > 0f)
                _musicLengthSeconds = streamLength;

            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable for Otto acceptance BGM.");

            _player = new AudioStreamPlayer
            {
                Name = "ReturnByDeathOttoAcceptanceMusic",
                Stream = stream,
                Bus = "Master",
                VolumeLinear = 1f,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            _player.Finished += OnMusicFinished;
            tree.Root.AddChild(_player);

            Volatile.Write(ref _pending, 0);
            Volatile.Write(ref _active, 1);
            Volatile.Write(ref _fading, 0);

            // 原版运行音乐与环境音共用控制器，但没有公开的“只停 BGM”方法。
            // 因此立即重建环境音，只让正在播放的背景音乐被专属曲替换。
            var musicController = NRunMusicController.Instance;
            if (musicController is not null && GodotObject.IsInstanceValid(musicController))
            {
                musicController.StopMusic();
                musicController.UpdateAmbience();
            }
            else
            {
                NAudioManager.Instance?.StopMusic();
            }
            _player.Play();
            ScheduleNaturalEndFade();
            ModLog.Write($"Started Otto acceptance BGM: {MusicFileName}.");
        }
        catch
        {
            StopSpecialMusic();
            throw;
        }
    }

    private static void ScheduleNaturalEndFade()
    {
        if (_player is null || !GodotObject.IsInstanceValid(_player))
            return;

        _tailFadeTween?.Kill();
        _tailFadeTween = _player.CreateTween();
        _tailFadeTween.SetProcessMode(Tween.TweenProcessMode.Idle);
        _tailFadeTween.SetIgnoreTimeScale(true);
        _tailFadeTween.TweenInterval(MathF.Max(0f, _musicLengthSeconds - FadeDurationSeconds));
        _tailFadeTween.TweenCallback(Callable.From(
            () => BeginFadeOut("track is nearing its end")));
    }

    private static void OnMusicFinished()
    {
        FinishAndRestoreNativeMusic("track finished");
    }

    private static void OnFadeTweenFinished()
    {
        ModLog.Write("Otto acceptance BGM fade tween completed.");
        FinishAndRestoreNativeMusic("fade complete");
    }

    private static void FinishAndRestoreNativeMusic(string reason)
    {
        if (Volatile.Read(ref _active) == 0 && Volatile.Read(ref _pending) == 0)
            return;

        StopSpecialMusic();
        RestoreNativeMusic();
        ModLog.Write($"Otto acceptance BGM ended; restored native music: {reason}.");
    }

    private static void StopSpecialMusic()
    {
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _active, 0);
        Volatile.Write(ref _fading, 0);

        if (_fadeTween is not null && GodotObject.IsInstanceValid(_fadeTween))
        {
            _fadeTween.Finished -= OnFadeTweenFinished;
            _fadeTween.Kill();
        }
        _fadeTween = null;

        if (_tailFadeTween is not null && GodotObject.IsInstanceValid(_tailFadeTween))
            _tailFadeTween.Kill();
        _tailFadeTween = null;

        if (_player is not null && GodotObject.IsInstanceValid(_player))
        {
            _player.Finished -= OnMusicFinished;
            _player.Stop();
            _player.QueueFree();
        }
        _player = null;

    }

    private static void RestoreNativeMusic()
    {
        // 音乐结束可能正好与离开战斗、进入火堆等场景切换重合。
        // 不在 Tween/Finished 回调中同步调用原版 UpdateMusic，避免和房间
        // 切换的音频/节点清理流程重入；延后一帧等场景状态稳定后恢复。
        if (Interlocked.Exchange(ref _nativeMusicRestoreQueued, 1) != 0)
            return;

        _ = RestoreNativeMusicNextFrameAsync();
    }

    private static async Task RestoreNativeMusicNextFrameAsync()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                return;

            await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitProcessFrame(
                tree.Root,
                System.Threading.CancellationToken.None);

            // 如果下一帧已有新的专属 BGM 状态，则让新的状态继续接管，
            // 不要把原版音乐插回正在播放的专属曲流程。
            if (IsSuppressingNativeRunMusic)
                return;

            var musicController = NRunMusicController.Instance;
            if (musicController is not null && GodotObject.IsInstanceValid(musicController))
            {
                musicController.UpdateMusic();
                ModLog.Write("Restored native run music on the frame after special BGM cleanup.");
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Native music restoration after special BGM failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _nativeMusicRestoreQueued, 0);
        }
    }

}

// Cosmetic audio from AncientFirstVisitSound is kept in the merged mod so
// the gameplay patches above and the replacement SFX share one Harmony owner.
[HarmonyPatch(typeof(NRunMusicController), nameof(NRunMusicController.UpdateMusic))]
internal static class OttoAcceptanceRunMusicPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NRunMusicController __instance) =>
        !OttoAcceptanceMusic.TrySuppressNativeRunMusic(__instance);
}

// OfferRoomEndRewards 是原版在战斗胜利后正式进入奖励/结算页面的入口。
// 比 UI 的 OnCombatWon 信号更靠近实际流程，也不会被失败、逃跑或普通页面切换触发。
[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.OfferRoomEndRewards))]
internal static class OttoAcceptanceCombatSettlementPatch
{
    [HarmonyPrefix]
    private static void Prefix() => OttoAcceptanceMusic.BeginAfterFirstCombat("entered first combat rewards/settlement");
}

// 保存退出回到主菜单，或直接退出游戏时，立刻释放专属播放器及内存状态。
// 这些状态不写入存档，因此下次启动会由原版恢复对应场景的背景音乐。
[HarmonyPatch(typeof(NGame), "LoadMainMenu")]
internal static class OttoAcceptanceMainMenuMusicResetPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        OttoAcceptanceMusic.StopImmediately();
        CosmeticAudio.Stop();
        RemEventState.OnLeftRun();
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
internal static class OttoAcceptanceQuitMusicResetPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        OttoAcceptanceMusic.StopImmediately();
        CosmeticAudio.Stop();
        RemEventState.OnLeftRun();
    }
}
