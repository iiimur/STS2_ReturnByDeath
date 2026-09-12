// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class RecoveryMarker
{
    private const string MarkerContent = "deathless-run-v4";
    private static readonly string MarkerPath = Path.Combine(ModDirectory, "deathless-run.pending");
    private static int _armed;
    private static int _preserveNativeRun;

    // 路径以 ModLog 的定义为准，避免两处各自推导。
    public static string ModDirectory => ModLog.ModDirectory;

    public static void Mark()
    {
        try
        {
            File.WriteAllText(MarkerPath, MarkerContent);
            Volatile.Write(ref _armed, 1);
        }
        catch { }
    }

    public static bool TryBegin()
    {
        if (Volatile.Read(ref _armed) == 1)
            return true;

        try
        {
            if (File.ReadAllText(MarkerPath) != MarkerContent)
                return false;
        }
        catch
        {
            return false;
        }

        Volatile.Write(ref _armed, 1);
        return true;
    }

    public static void Complete()
    {
        Volatile.Write(ref _armed, 0);
        try { File.Delete(MarkerPath); } catch { }
    }

    public static void ReleaseForRetry() => Volatile.Write(ref _armed, 0);

    public static bool ShouldPreserveNativeRun => Volatile.Read(ref _preserveNativeRun) != 0;

    public static void PreserveNativeRun() => Volatile.Write(ref _preserveNativeRun, 1);

    public static void ReleaseNativeRunPreservation() => Volatile.Write(ref _preserveNativeRun, 0);

    public static void StopRecoveryForArchitect()
    {
        // The Architect is the genuine end of the run. Let native death
        // settlement proceed and remove any stale continuation marker.
        ReleaseNativeRunPreservation();
        Complete();
    }

    public static void Clear()
    {
        Complete();
        ReleaseNativeRunPreservation();
        CheckpointStore.Clear();
        EncounterJournalStore.Clear();
    }
}

// 检查点的“基准”是在先古选项完成后保存的；获得卡牌与升级在发生当刻
// 增量写入这个基准，死亡时不再复制整副牌组。金币、遗物、删牌与附魔等
// 未登记变化仍按检查点回滚。
