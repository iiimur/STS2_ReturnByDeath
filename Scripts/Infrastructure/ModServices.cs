// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class ModLog
{
    public static string ModDirectory =>
        Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? AppContext.BaseDirectory;

    private static readonly string[] StateFileNames =
    {
        "deathless-run.checkpoint.json",
        "deathless-run.checkpoint.version",
        "deathless-run.encounter-journal.json",
        "deathless-run.fixed-rooms.json",
        "deathless-run.encounter-replay",
        "deathless-run.explored-nodes.json",
        "deathless-run.first-recovery-audio-used",
        "deathless-run.pending",
        "deathless-run.pending-restored-cards.json",
        "return-by-death.act2-ancient.json",
        "return-by-death.amnesia.json",
        "return-by-death.curse-budget.json",
        "return-by-death.echidna-visit.json",
        "return-by-death.greed-finale.json",
        "return-by-death.if-achievements.json",
        "return-by-death.otto-accepted",
        "return-by-death.otto-checkpoint-pending",
        "return-by-death.otto-event-used",
        "return-by-death.otto-pending-cleanup",
        "return-by-death.rem-state.json",
        "return-by-death.reward-snapshot.json",
        "return-by-death.route-state.json",
        "return-by-death.true-playtime.json"
    };
    private static string? _stateDirectory;
    private static int _stateStorageReady;

    // 状态文件不能放在 mods 目录：创意工坊更新可能替换整个 mod 文件夹，
    // 游戏启动时也会把其中每个 JSON 都当成潜在的 mod 清单扫描。
    public static string StateDirectory
    {
        get
        {
            EnsureStateStorage();
            return _stateDirectory!;
        }
    }

    public static string StateFile(string fileName) =>
        Path.Combine(StateDirectory, fileName);

    public static void EnsureStateStorage()
    {
        if (Interlocked.CompareExchange(ref _stateStorageReady, 1, 0) != 0)
            return;

        try
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = AppContext.BaseDirectory;

            _stateDirectory = Path.Combine(appData, "SlayTheSpire2", "ReturnByDeath");
            Directory.CreateDirectory(_stateDirectory);

            // 只复制旧状态，不在未获用户确认前删除旧文件。新版本已经改为
            // 从 StateDirectory 读写，因此即使旧文件被工坊更新覆盖也不会影响
            // 已迁移的检查点；旧文件留在原处仅用于安全回退和人工清理。
            foreach (var fileName in StateFileNames)
            {
                var legacyPath = Path.Combine(ModDirectory, fileName);
                var statePath = Path.Combine(_stateDirectory, fileName);
                if (File.Exists(statePath) || !File.Exists(legacyPath))
                    continue;

                try { File.Copy(legacyPath, statePath); }
                catch { }
            }
        }
        catch
        {
            // 极端权限环境回退到 mod 目录，保证状态读写不会阻断游戏启动。
            _stateDirectory = ModDirectory;
        }
    }

    private static string LogPath => Path.Combine(ModDirectory, "return-by-death.log");

    public static void Write(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{System.Environment.NewLine}"); }
        catch { }
    }
}

// 运行包中的素材按类型分目录保存，避免视频、音效和状态文件混在 mod 根目录。
internal static class ModAssetPaths
{
    public static string Audio(string fileName) =>
        Path.Combine(ModLog.ModDirectory, "音效", fileName);

    public static string Video(string fileName) =>
        Path.Combine(ModLog.ModDirectory, "视频", fileName);

    public static string Image(string fileName) =>
        Path.Combine(ModLog.ModDirectory, fileName);
}

// 恢复音效与装饰音效共用同一套“加载缓存 + 单例播放器”管理；
// 两个实例各持一个 AudioStreamPlayer，保持不同来源的音效可以同时发声。
internal sealed class WavPlayer
{
    private readonly string _logLabel;
    private readonly Dictionary<string, AudioStreamWav> _streams = new(StringComparer.Ordinal);
    private AudioStreamPlayer? _player;
    private Action? _onFinished;

    public WavPlayer(string logLabel) => _logLabel = logLabel;

    public bool TryPlay(string fileName, float volume = 1f, Action? onFinished = null)
    {
        try
        {
            if (!_streams.TryGetValue(fileName, out var stream))
            {
                var path = ModAssetPaths.Audio(fileName);
                stream = AudioStreamWav.LoadFromFile(path);
                if (stream is null)
                {
                    ModLog.Write($"Could not load {_logLabel} WAV: {path}");
                    return false;
                }

                _streams[fileName] = stream;
                ModLog.Write($"Loaded {_logLabel} WAV: {path}");
            }

            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree is null)
            {
                ModLog.Write($"Could not find the active SceneTree for {_logLabel} audio.");
                return false;
            }

            if (_player is null || !GodotObject.IsInstanceValid(_player))
            {
                _player = new AudioStreamPlayer
                {
                    Stream = stream,
                    Bus = "Master",
                    ProcessMode = Node.ProcessModeEnum.Always
                };
                tree.Root.AddChild(_player);
            }
            else
            {
                _player.Stream = stream;
            }

            // 单voice播放器：换音频时清掉上一次的完成回调，避免旧回调误触发。
            if (_onFinished is not null)
            {
                _player.Finished -= InvokeFinished;
                _onFinished = null;
            }
            if (onFinished is not null)
            {
                _onFinished = onFinished;
                _player.Finished += InvokeFinished;
            }

            _player.VolumeDb = Mathf.LinearToDb(Math.Clamp(volume, 0f, 1f));
            _player.Play();
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Write($"{_logLabel} audio playback failed: {exception}");
            return false;
        }
    }

    private void InvokeFinished()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player))
            _player.Finished -= InvokeFinished;
        _onFinished?.Invoke();
        _onFinished = null;
    }

    // 播放器挂在场景根上，原版回到主菜单时不会自动释放它。
    // 由主菜单/退出补丁调用，直接截停，避免音效残留到菜单音乐中。
    public void Stop()
    {
        if (_player is null || !GodotObject.IsInstanceValid(_player))
            return;

        _player.Stop();
        _player.QueueFree();
        _player = null;
    }
}

internal static class RecoveryAudio
{
    private const string FirstAudioFileName = "重生音效-首次.wav";
    private const string NormalAudioFileName = "重生音效-常规.wav";
    private static readonly string FirstAudioUsedMarkerPath = ModLog.StateFile(
        "deathless-run.first-recovery-audio-used");
    private static readonly WavPlayer Player = new("recovery");

    // 失忆回归强制使用首次回归音效，且不改变首归标记。
    public static bool PlayFirstRecoveryAudio() => Player.TryPlay(FirstAudioFileName);

    public static bool TryPlayRecovery(SerializableRun checkpoint)
    {        var runKey = CheckpointStore.GetRunKey(checkpoint);
        if (!WasFirstAudioUsedForRun(runKey) && Player.TryPlay(FirstAudioFileName))
        {
            try
            {
                File.WriteAllText(FirstAudioUsedMarkerPath, $"deathless-run-first-recovery-audio-v2:{runKey}");
                ModLog.Write("Played first recovery WAV and marked it as used.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Could not persist first recovery audio marker: {exception.Message}");
            }

            return true;
        }

        return Player.TryPlay(NormalAudioFileName);
    }

    private static bool WasFirstAudioUsedForRun(string runKey)
    {
        try
        {
            return string.Equals(
                File.ReadAllText(FirstAudioUsedMarkerPath),
                $"deathless-run-first-recovery-audio-v2:{runKey}",
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
