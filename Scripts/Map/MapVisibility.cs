// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 地图瘦身。节点分三类：
//   已探索——走过的房间（当前时间线 VisitedMapCoords + 既往时间线的记忆）；
//   探索中——当前所在房间与历代死亡/放弃的位置（探索记忆单独记录）；
//   未探索——其余节点，包括当前可选的下一批子节点（游戏必需，保持可见）。
//   起点与 Boss 节点（双 Boss 模式下两个）是字典外的独立节点，始终显示，
//   但通向它们的路线仍按通用规则过滤。
// 路径线显示两类：两端都可见的边（走过的、当前可选的、通向死亡位置的），
// 以及探索记忆中记录的、玩家在选择界面见过的路线（含未选择的分支）。
// 通向未探索区域的线与来路方向的回头线一律隐藏。当前节点取
// RunState.CurrentMapPoint；尚未选择本层第一个节点时以 StartingMapPoint 为中心。
// 每次地图构建、打开、刷新视觉或完成移动后都会重算。过滤只影响显示，
// 失败时保持原版全图可见。
internal static class MapNodeVisibilityFilter
{
    private static readonly string[] SpecialPointNodeFields =
    {
        "_startingPointNode",
        "_bossPointNode",
        "_secondBossPointNode"
    };

    // 测试用“全知之眼”（控制台命令 open_eye 切换）：不过滤地图显示。
    public static bool OpenEyeEnabled { get; private set; }

    public static void ToggleOpenEye()
    {
        OpenEyeEnabled = !OpenEyeEnabled;
        ModLog.Write($"Open eye toggled: {OpenEyeEnabled}.");
        RefreshOpenMap();
    }

    // 怠惰线使用显式开启而不是 Toggle：即使玩家之前已经手动开图，也不会误关。
    public static void EnableOpenEye()
    {
        if (!OpenEyeEnabled)
        {
            OpenEyeEnabled = true;
            ModLog.Write("Open eye automatically enabled for the Sloth route.");
        }

        RefreshOpenMap();
    }

    // 开图是本局临时显示状态；新局不能继承上一局怠惰线的自由视野。
    public static void ResetOpenEye()
    {
        OpenEyeEnabled = false;
    }

    private static void RefreshOpenMap()
    {
        try
        {
            if (NRun.Instance?.GlobalUi?.MapScreen is { } mapScreen)
            {
                Apply(mapScreen);
                // 本地版本将该刷新方法设为内部成员；反射调用仅刷新当前地图的
                // 点击状态，不改变原版旅行流程。
                AccessTools.Method(typeof(NMapScreen), "RecalculateTravelability")?.Invoke(mapScreen, null);
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Open eye map refresh failed: {exception.Message}");
        }
    }

    private static void SetEntireMapVisible(NMapScreen screen)
    {
        if (AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary")?
                .GetValue(screen) is Dictionary<MapCoord, NMapPoint> points)
        {
            foreach (var (coord, node) in points)
            {
                if (node is not null && GodotObject.IsInstanceValid(node))
                    node.Visible = true;
            }
        }

        foreach (var fieldName in SpecialPointNodeFields)
        {
            if (AccessTools.Field(typeof(NMapScreen), fieldName)?.GetValue(screen) is not NMapPoint node ||
                !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            node.Visible = true;
        }

        if (AccessTools.Field(typeof(NMapScreen), "_paths")?.GetValue(screen)
                is not Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths)
        {
            return;
        }

        foreach (var ((_, _), lineNodes) in paths)
        {
            foreach (var line in lineNodes)
            {
                if (line is not null && GodotObject.IsInstanceValid(line))
                    line.Visible = true;
            }
        }
    }

    public static void Apply(NMapScreen screen)
    {
        try
        {
            var runManager = RunManager.Instance;
            if (runManager is null || !runManager.IsSingleplayerOrFakeMultiplayer)
                return;

            var state = runManager.DebugOnlyGetState();
            var map = state?.Map;
            if (state is null || map is null)
                return;

            // 测试用“全知之眼”：开启后不做任何过滤，本层全部节点与路径线可见。
            if (OpenEyeEnabled)
            {
                SetEntireMapVisible(screen);
                return;
            }

            var focus = state.CurrentMapPoint ?? map.StartingMapPoint;
            if (focus is null)
                return;

            var coordField = AccessTools.Field(typeof(MapPoint), "coord");
            var allowedCoords = new HashSet<MapCoord>();

            void Allow(MapPoint? point)
            {
                if (point is not null && coordField?.GetValue(point) is MapCoord coord)
                    allowedCoords.Add(coord);
            }

            // 起点与已探索节点（当前时间线 + 既往时间线的记忆）保持可见；
            // 探索中节点（当前所在房间与历代死亡位置）也并入可见集合；
            // 当前节点的全部子节点（下一批可选节点）同样可见。
            Allow(map.StartingMapPoint);
            foreach (var visited in state.VisitedMapCoords)
                allowedCoords.Add(visited);
            foreach (var remembered in ExploredNodesMemory.GetForAct(state.CurrentActIndex))
                allowedCoords.Add(remembered);
            if (state.CurrentMapCoord is { } currentCoord)
                allowedCoords.Add(currentCoord);
            Allow(focus);
            foreach (var child in focus.Children)
                Allow(child);

            var points = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary")?
                .GetValue(screen) as Dictionary<MapCoord, NMapPoint>;
            if (points is not null)
            {
                foreach (var (coord, node) in points)
                {
                    if (node is null || !GodotObject.IsInstanceValid(node))
                        continue;

                    node.Visible = allowedCoords.Contains(coord);
                }
            }

            // 起点、Boss 与第二 Boss 是字典之外的独立节点：起点是足迹的起点；
            // Boss 在每层地图上始终显示（双 Boss 模式下有两个），但通向 Boss 的
            // 路线仍按通用规则过滤，到达之前不会显示。
            foreach (var fieldName in SpecialPointNodeFields)
            {
                if (AccessTools.Field(typeof(NMapScreen), fieldName)?.GetValue(screen) is not NMapPoint node ||
                    !GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                node.Visible = true;
            }

            var paths = AccessTools.Field(typeof(NMapScreen), "_paths")?.GetValue(screen)
                as Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>;
            if (paths is null)
                return;

            // 路径线显示规则：
            //   两端都可见——走过的边、当前可选的边、通向死亡/放弃位置的边；
            //   按记录匹配——玩家在选择界面见过的路线（含未选择的分支），
            //   数据来自选路时的记录，不做任何方向推断。
            // 其余（未探索区域内部的线、来路方向的回头线）一律隐藏。
            var recordedRoutes = ExploredNodesMemory.GetRoutesForAct(state.CurrentActIndex);
            foreach (var ((start, end), lineNodes) in paths)
            {
                var visible = (allowedCoords.Contains(start) && allowedCoords.Contains(end)) ||
                              recordedRoutes.Contains((start.col, start.row, end.col, end.row)) ||
                              recordedRoutes.Contains((end.col, end.row, start.col, start.row));
                foreach (var line in lineNodes)
                {
                    if (line is not null && GodotObject.IsInstanceValid(line))
                        line.Visible = visible;
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Map node visibility filter failed: {exception}");
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class MapFilterSetMapPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) => MapNodeVisibilityFilter.Apply(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.RefreshAllPointVisuals))]
internal static class MapFilterRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) => MapNodeVisibilityFilter.Apply(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class MapFilterOpenPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) => MapNodeVisibilityFilter.Apply(__instance);
}

// 移动动画完成后地图可能仍然打开；此时重算一次，让新节点的邻居立即成为可见范围。
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
internal static class MapFilterTravelPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance, ref Task __result)
    {
        __result = ApplyAfterTravelAsync(__result, __instance);
    }

    private static async Task ApplyAfterTravelAsync(Task travelTask, NMapScreen screen)
    {
        try
        {
            await travelTask;
        }
        finally
        {
            MapNodeVisibilityFilter.Apply(screen);
        }
    }
}

// MapTravel 会询问这个原版 Hook 决定是否允许无视常规路线自由移动。
// 怠惰线在进入时已自动开图，此处补上实际的自由旅行权限。
internal static class ExploredNodesMemory
{
    private sealed class CoordRecord
    {
        public int Col { get; set; }
        public int Row { get; set; }
    }

    private sealed class RouteRecord
    {
        public CoordRecord From { get; set; } = new();
        public CoordRecord To { get; set; } = new();
    }

    private sealed class ActMemory
    {
        public List<CoordRecord> Explored { get; set; } = new();
        public List<CoordRecord> Exploring { get; set; } = new();
        public List<RouteRecord> Routes { get; set; } = new();
    }

    private sealed class MemoryFile
    {
        public Dictionary<int, ActMemory> Acts { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static readonly string MemoryPath = Path.Combine(
        ModLog.ModDirectory,
        "deathless-run.explored-nodes.json");
    private static MemoryFile? _file;
    private static readonly System.Reflection.FieldInfo? PointCoordField =
        AccessTools.Field(typeof(MapPoint), "coord");

    private static MapCoord? GetPointCoord(MapPoint? point) =>
        point is not null && PointCoordField?.GetValue(point) is MapCoord coord ? coord : null;

    // 玩家选路（进入新节点）时调用：CurrentMapCoord 仍是刚完成的节点，
    // 把它展示过的全部分支路线记录下来，不区分本次是否选中。
    public static void RecordRoute(RunState state, MapCoord chosenCoord)
    {
        // 失忆时间线：地图路线不记录。
        if (AmnesiaState.TimelineHidden)
            return;

        lock (Sync)
        {
            try
            {
                var map = state.Map;
                if (map is null)
                    return;

                var from = state.CurrentMapCoord ?? GetPointCoord(map.StartingMapPoint);
                if (from is null)
                    return;

                var completed = map.GetPoint(from.Value) ?? map.StartingMapPoint;
                if (completed is null)
                    return;

                var file = EnsureLoaded();
                if (!file.Acts.TryGetValue(state.CurrentActIndex, out var act))
                {
                    act = new ActMemory();
                    file.Acts[state.CurrentActIndex] = act;
                }

                var added = false;
                foreach (var child in completed.Children)
                {
                    var to = GetPointCoord(child);
                    if (to is null)
                        continue;

                    if (act.Routes.Any(r => r.From.Col == from.Value.col && r.From.Row == from.Value.row &&
                                            r.To.Col == to.Value.col && r.To.Row == to.Value.row))
                    {
                        continue;
                    }

                    act.Routes.Add(new RouteRecord
                    {
                        From = new CoordRecord { Col = from.Value.col, Row = from.Value.row },
                        To = new CoordRecord { Col = to.Value.col, Row = to.Value.row }
                    });
                    added = true;
                }

                if (added)
                {
                    File.WriteAllText(MemoryPath, JsonSerializer.Serialize(file));
                    ModLog.Write($"Recorded offered map routes from ({from.Value.col},{from.Value.row}) in act {state.CurrentActIndex}.");
                }
            }
            catch (Exception exception)
            {
                ModLog.Write($"Route memory record failed: {exception.Message}");
            }
        }
    }

    public static void Record(RunState state)
    {
        // 失忆时间线：地图足迹不记录。
        if (AmnesiaState.TimelineHidden)
            return;

        lock (Sync)
        {
            try
            {
                var file = EnsureLoaded();
                if (!file.Acts.TryGetValue(state.CurrentActIndex, out var act))
                {
                    act = new ActMemory();
                    file.Acts[state.CurrentActIndex] = act;
                }

                // 已探索：本条时间线走过的房间；多次死亡按层合并累计。
                foreach (var coord in state.VisitedMapCoords)
                {
                    if (act.Explored.All(c => c.Col != coord.col || c.Row != coord.row))
                        act.Explored.Add(new CoordRecord { Col = coord.col, Row = coord.row });
                }

                // 探索中：时间线结束时所在的房间。
                if (state.CurrentMapCoord is { } current &&
                    act.Exploring.All(c => c.Col != current.col || c.Row != current.row))
                {
                    act.Exploring.Add(new CoordRecord { Col = current.col, Row = current.row });
                }

                File.WriteAllText(MemoryPath, JsonSerializer.Serialize(file));
                ModLog.Write($"Recorded map memory before death: act {state.CurrentActIndex}, " +
                             $"{act.Explored.Count} explored, {act.Exploring.Count} exploring.");
            }
            catch (Exception exception)
            {
                ModLog.Write($"Explored-nodes memory record failed: {exception.Message}");
            }
        }
    }

    // 已探索与探索中的节点都会显示在地图上，合并返回即可；
    // 路径线由地图过滤按“两端都可见”规则推导。
    public static List<MapCoord> GetForAct(int act)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (!file.Acts.TryGetValue(act, out var memory))
                return new List<MapCoord>();

            var result = new List<MapCoord>();
            foreach (var coord in memory.Explored.Concat(memory.Exploring))
            {
                var value = new MapCoord { col = coord.Col, row = coord.Row };
                if (result.All(c => c.col != value.col || c.row != value.row))
                    result.Add(value);
            }

            return result;
        }
    }

    // 玩家在选择界面见过的全部路线（含未选择的分支）。地图过滤按记录匹配，
    // 键的端点顺序未知，因此匹配时正反两个方向都查。
    public static HashSet<(int FromCol, int FromRow, int ToCol, int ToRow)> GetRoutesForAct(int act)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            var result = new HashSet<(int, int, int, int)>();
            if (file.Acts.TryGetValue(act, out var memory))
            {
                foreach (var route in memory.Routes)
                    result.Add((route.From.Col, route.From.Row, route.To.Col, route.To.Row));
            }

            return result;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _file = new MemoryFile();
            try { File.Delete(MemoryPath); } catch { }
        }
    }

    private static MemoryFile EnsureLoaded()
    {
        if (_file is not null)
            return _file;

        try
        {
            _file = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(MemoryPath)) ?? new MemoryFile();
        }
        catch
        {
            _file = new MemoryFile();
        }

        return _file;
    }
}
