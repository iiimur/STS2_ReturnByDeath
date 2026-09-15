// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 接受奥托后的专属 BGM。原版 FMOD 背景音乐不再被停止或抑制，只通过
// NAudioManager 的 BGM 音量接口压低到 20%；附加音乐/视频播放完毕后再恢复。
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
    private static int _nativeBgmDuckHeld;

    public static bool IsSuppressingNativeRunMusic =>
        Volatile.Read(ref _pending) != 0 || Volatile.Read(ref _active) != 0;

    public static bool IsActive => Volatile.Read(ref _active) != 0;

    public static bool AcquireNativeBgmDuck() {
        if (!NativeBgmDucker.Acquire("Otto acceptance music"))
            return false;

        Volatile.Write(ref _nativeBgmDuckHeld, 1);
        return true;
    }

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

            if (Volatile.Read(ref _nativeBgmDuckHeld) == 0)
                AcquireNativeBgmDuck();
            NativeBgmDucker.Reapply();

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

        var players = new List<AudioStreamPlayer>();
        if (_player is not null && GodotObject.IsInstanceValid(_player))
            players.Add(_player);

        // 防御旧版本在重载/异常切换中留下的同名播放器：即使静态引用已经
        // 被覆盖，也不能让旧 AudioStreamPlayer 继续在后台播放。
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root is not null)
        {
            foreach (var node in tree.Root.FindChildren("*OttoAcceptanceMusic*", nameof(AudioStreamPlayer), true, false))
            {
                if (node is AudioStreamPlayer player && GodotObject.IsInstanceValid(player) && !players.Contains(player))
                    players.Add(player);
            }
        }

        foreach (var player in players)
        {
            player.Finished -= OnMusicFinished;
            player.Stop();
            player.QueueFree();
        }
        _player = null;

        if (Interlocked.Exchange(ref _nativeBgmDuckHeld, 0) != 0)
            NativeBgmDucker.Release("Otto acceptance music finished");
    }
}

// 共享的原生 BGM 音量租约。租约计数允许“奥托视频 + 接受后的专属音乐”
// 交接时保持持续压低；只有最后一个附加内容结束，才恢复玩家原本的 BGM 音量。
internal static class NativeBgmDucker
{
    private const float DuckFactor = 0.2f;
    private static readonly object Sync = new();
    private static float? _savedVolume;
    private static int _leases;

    public static bool Acquire(string reason)
    {
        lock (Sync)
        {
            try
            {
                var settings = SaveManager.Instance?.SettingsSave;
                if (settings is null)
                    return false;

                if (_leases == 0)
                {
                    _savedVolume = settings.VolumeBgm;
                    NAudioManager.Instance?.SetBgmVol(_savedVolume.Value * DuckFactor);
                    ModLog.Write($"Native BGM ducked to 20% of {_savedVolume.Value:0.00}: {reason}.");
                }

                _leases++;
                return true;
            }
            catch (Exception exception)
            {
                ModLog.Write($"Native BGM duck failed: {exception.Message}");
                return false;
            }
        }
    }

    public static void Release(string reason)
    {
        lock (Sync)
        {
            if (_leases <= 0)
                return;

            _leases--;
            if (_leases != 0)
                return;

            try
            {
                if (_savedVolume is { } volume)
                    NAudioManager.Instance?.SetBgmVol(volume);
                ModLog.Write($"Native BGM volume restored: {reason}.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Native BGM restore failed: {exception.Message}");
            }
            finally
            {
                _savedVolume = null;
            }
        }
    }

    public static void Reapply()
    {
        lock (Sync)
        {
            if (_leases == 0 || _savedVolume is not { } volume)
                return;

            try
            {
                NAudioManager.Instance?.SetBgmVol(volume * DuckFactor);
            }
            catch (Exception exception)
            {
                ModLog.Write($"Native BGM duck reapply failed: {exception.Message}");
            }
        }
    }

    public static void ForceRestore()
    {
        lock (Sync)
        {
            if (_leases == 0)
                return;

            _leases = 1;
            Release("forced cleanup");
        }
    }
}

[HarmonyPatch(typeof(NRunMusicController), nameof(NRunMusicController.UpdateMusic))]
internal static class NativeBgmDuckAfterNativeMusicPatch
{
    [HarmonyPostfix]
    private static void Postfix() => NativeBgmDucker.Reapply();
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
        NativeBgmDucker.ForceRestore();
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
        NativeBgmDucker.ForceRestore();
        CosmeticAudio.Stop();
        RemEventState.OnLeftRun();
    }
}
