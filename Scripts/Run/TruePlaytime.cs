// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 原版 RunTime 存在运行快照里，死亡回归会随检查点回退。这里把每次回归
// “丢失”的秒数（死亡时 RunTime − 检查点 RunTime）累计到 mod 文件；
// 真实总时长 = 原版 RunTime + 累计丢失量，显示在顶栏计时器旁边，
// 死亡回归不回档。新开局与失忆回归（全新时间线）清零。
internal static class TruePlaytimeTracker
{
    private sealed class PlaytimeFile
    {
        public long ExtraSeconds { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(
        ModLog.ModDirectory,
        "return-by-death.true-playtime.json");
    private static PlaytimeFile? _file;

    public static long ExtraSeconds
    {
        get
        {
            lock (Sync)
            {
                return EnsureLoaded().ExtraSeconds;
            }
        }
    }

    public static void AddLostSeconds(long seconds)
    {
        if (seconds <= 0)
            return;

        lock (Sync)
        {
            var file = EnsureLoaded();
            file.ExtraSeconds += seconds;
            Save();
            ModLog.Write($"True playtime: {seconds}s lost to recovery, total extra {file.ExtraSeconds}s.");
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _file = new PlaytimeFile();
            try { File.Delete(FilePath); } catch { }
        }
    }

    private static PlaytimeFile EnsureLoaded()
    {
        if (_file is not null)
            return _file;

        try
        {
            _file = JsonSerializer.Deserialize<PlaytimeFile>(File.ReadAllText(FilePath)) ?? new PlaytimeFile();
        }
        catch
        {
            _file = new PlaytimeFile();
        }

        return _file;
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_file));
        }
        catch (Exception exception)
        {
            ModLog.Write($"True playtime save failed: {exception.Message}");
        }
    }
}

// 代理模式是本局的临时测试开关：开启后，遭遇预告中已经完成的战斗会
// 直接结算，并扣除该遭遇最近一次记录的净掉血（无记录时不扣）；新开局自动关闭。
// 开关只在“死掉的那条命胜利过至少一场战斗、之后死亡回归”时展示——回归
// 落地即展示并默认开启；条件不满足时隐藏并强制关闭，避免残留自动结算状态。
[HarmonyPatch(typeof(NRunTimer), "OnTimerTimeout")]
internal static class TruePlaytimeDisplayPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRunTimer __instance)
    {
        try
        {
            ProxyModeToggle.Ensure(__instance);
            CurseBudgetDisplay.Ensure(__instance);
            var runManager = RunManager.Instance;
            if (runManager is null || runManager.IsGameOver)
                return;

            var extra = TruePlaytimeTracker.ExtraSeconds;
            // 失忆时间线：总时间隐藏（数据照常累计，只是不显示）。
            if (extra <= 0 || AmnesiaState.TimelineHidden)
                return;

            if (AccessTools.Field(typeof(NRunTimer), "_timerLabel")?.GetValue(__instance) is not MegaLabel label)
                return;

            var native = TimeFormatting.Format(runManager.RunTime);
            var total = TimeFormatting.Format(runManager.RunTime + extra);
            label.SetTextAutoSize($"{native} / {total}");
        }
        catch (Exception exception)
        {
            ModLog.Write($"True playtime display failed: {exception.Message}");
        }
    }
}
