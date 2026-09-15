// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 各条 IF 路线均属于当前一局游戏而非全局设置。单独写入 mod 目录，能够跨保存退出、
// 回归重载继续生效；新开局会清空它，因此不会污染下一局。
internal static class RouteState
{
    private const string StateFileName = "return-by-death.route-state.json";
    private static readonly string StatePath = ModLog.StateFile(StateFileName);
    private static readonly object _gate = new();
    private static int _loaded;
    private static int _ifRoute;
    private static int _prideRoute;
    private static int _slothRoute;
    private static int _wrathRoute;
    private static int _recoveredFromDeath;
    private static int _atRestedFireCheckpoint;
    private static int _specialBannerPending;

    // 本局第一次死亡回归的层首报幕是否待替换。失忆回归也会设置此标记。

    public static bool IsIfRoute
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _ifRoute) != 0;
        }
    }

    public static bool IsPrideRoute
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _prideRoute) != 0;
        }
    }

    public static bool IsSlothRoute
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _slothRoute) != 0;
        }
    }

    public static bool IsWrathRoute
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _wrathRoute) != 0;
        }
    }

    // 本次运行是否发生过死亡回归（正常回归与失忆回归均算）。
    public static bool IsRecoveredFromDeath
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _recoveredFromDeath) != 0;
        }
    }

    // 死亡回归的落点是否为“休息过的火堆”检查点。该状态只在恢复落地时
    // 写入（原版 LoadRun 会为当前房间追加一条不带 HEAL 的新历史条目，
    // 因此不能在事后从历史记录反推），前进到新地图点时由蕾姆事件清除。
    public static bool IsAtRestedFireCheckpoint
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _atRestedFireCheckpoint) != 0;
        }
    }

    public static void SetAtRestedFireCheckpoint(bool value)
    {
        EnsureLoaded();
        Volatile.Write(ref _atRestedFireCheckpoint, value ? 1 : 0);
        Save();
    }

    public static void EnterPrideRoute()
    {
        EnsureLoaded();
        Interlocked.Exchange(ref _ifRoute, 1);
        Interlocked.Exchange(ref _prideRoute, 1);
        TombstoneEntry.HideForIfRoute();
        Save();
        ModLog.Write("Pride ending entered: IF route=true, Pride route=true.");
    }

    public static void EnterSlothRoute()
    {
        EnsureLoaded();
        Interlocked.Exchange(ref _ifRoute, 1);
        Interlocked.Exchange(ref _slothRoute, 1);
        TombstoneEntry.HideForIfRoute();
        Save();
        // 怠惰线从此不再有未知路线：自动打开全图，并允许自由移动。
        MapNodeVisibilityFilter.EnableOpenEye();
        ModLog.Write("Sloth ending entered: IF route=true, Sloth route=true.");
    }

    public static void EnterWrathRoute()
    {
        EnsureLoaded();
        Interlocked.Exchange(ref _ifRoute, 1);
        Interlocked.Exchange(ref _wrathRoute, 1);
        TombstoneEntry.HideForIfRoute();
        Save();
        ModLog.Write("Wrath ending entered: IF route=true, Wrath route=true.");
    }

    // 记录一次死亡回归；只有本局第一次回归会安排一次特殊标题报幕。
    public static void MarkRecoveredFromDeath(int actIndex)
    {
        EnsureLoaded();
        if (Interlocked.CompareExchange(ref _recoveredFromDeath, 1, 0) == 0)
            Volatile.Write(ref _specialBannerPending, 1);
        Save();
    }

    // 原子消费本局唯一一次特殊报幕机会。
    public static bool ConsumeSpecialBanner()
    {
        EnsureLoaded();
        return Interlocked.Exchange(ref _specialBannerPending, 0) != 0;
    }

    public static void ResetForNewRun()
    {
        Volatile.Write(ref _loaded, 1);
        Volatile.Write(ref _ifRoute, 0);
        Volatile.Write(ref _prideRoute, 0);
        Volatile.Write(ref _slothRoute, 0);
        Volatile.Write(ref _wrathRoute, 0);
        Volatile.Write(ref _recoveredFromDeath, 0);
        Volatile.Write(ref _atRestedFireCheckpoint, 0);
        Volatile.Write(ref _specialBannerPending, 0);
        try { File.Delete(StatePath); }
        catch (Exception exception) { ModLog.Write($"Could not clear route state for new run: {exception.Message}"); }
    }

    private static void EnsureLoaded()
    {
        if (Interlocked.CompareExchange(ref _loaded, 1, 0) != 0)
            return;

        try
        {
            if (!File.Exists(StatePath))
                return;

            var saved = JsonSerializer.Deserialize<SavedRouteState>(File.ReadAllText(StatePath));
            if (saved is null)
                return;

            Volatile.Write(ref _ifRoute, saved.IfRoute ? 1 : 0);
            Volatile.Write(ref _prideRoute, saved.PrideRoute ? 1 : 0);
            Volatile.Write(ref _slothRoute, saved.SlothRoute ? 1 : 0);
            Volatile.Write(ref _wrathRoute, saved.WrathRoute ? 1 : 0);
            Volatile.Write(ref _recoveredFromDeath, saved.RecoveredFromDeath ? 1 : 0);
            Volatile.Write(ref _atRestedFireCheckpoint, saved.AtRestedFireCheckpoint ? 1 : 0);
            Volatile.Write(ref _specialBannerPending, 0);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not load route state; using normal route: {exception.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var state = new SavedRouteState
            {
                IfRoute = Volatile.Read(ref _ifRoute) != 0,
                PrideRoute = Volatile.Read(ref _prideRoute) != 0,
                SlothRoute = Volatile.Read(ref _slothRoute) != 0,
                WrathRoute = Volatile.Read(ref _wrathRoute) != 0,
                RecoveredFromDeath = Volatile.Read(ref _recoveredFromDeath) != 0,
                AtRestedFireCheckpoint = Volatile.Read(ref _atRestedFireCheckpoint) != 0
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not save route state: {exception.Message}");
        }
    }

    private sealed class SavedRouteState
    {
        public bool IfRoute { get; set; }
        public bool PrideRoute { get; set; }
        public bool SlothRoute { get; set; }
        public bool WrathRoute { get; set; }
        public bool RecoveredFromDeath { get; set; }
        public bool AtRestedFireCheckpoint { get; set; }
        public int[]? RecoveredActs { get; set; }
    }
}
