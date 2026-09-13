// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
internal static class EncounterJournalCoordinatePatch
{
    [HarmonyPrefix]
    private static void Prefix(RunManager __instance, MapCoord coord)
    {
        EncounterJournalStore.SetPendingCoordinate(coord);
        // 此刻 CurrentMapCoord 仍是刚完成的节点：把它的全部分支路线记入探索记忆。
        if (__instance.IsSingleplayerOrFakeMultiplayer &&
            __instance.DebugOnlyGetState() is { } state)
        {
            ExploredNodesMemory.RecordRoute(state, coord);
        }
    }

}

[HarmonyPatch(typeof(EncounterModel), nameof(EncounterModel.GenerateMonstersWithSlots))]
internal static class EncounterJournalPatch
{
    [HarmonyPostfix]
    private static void Postfix(EncounterModel __instance, IRunState runState)
    {
        if (ArchitectFinaleState.Observe(__instance))
            return;

        EncounterJournalStore.RecordMonsters(runState, __instance);
    }
}

// 遭遇预告：复用原版“历史记录”条目节点（NActHistoryEntry/NMapPointHistoryEntry，
// 与记忆面板同一套 UI），从记忆面板的横向排列改为在预告位置纵向排列。
// 数据源是记忆面板的记忆：每条记忆是一次完整的生命结算记录（出生→存档点
// →死亡），悬浮即可看到结算生命、金币、敌人、失去的生命、回合、获得/跳过
// 的奖励等原生内容。合并按（层, 类别, 类别内序号）进行：战斗的出现顺序与
// 问号的类型顺序各自固定（每进一个同类房间消耗序列下一项），同序号冲突时
// 保留较新记忆（“最新”原则）；路线只影响战斗与问号的交错方式，因此联动
// 不用到访顺序，改由悬浮时按当前真实进度现算序号。分四组展示（普通怪物、
// 精英怪物、问号、商店），组标题不再显示以留出横向空间；问号房打出的战斗
// 同时留在对应怪物分组。
internal static class EncounterPreviewOverlay
{
    private static VBoxContainer? _container;

    // 预告行（NActHistoryEntry 里的每个 NMapPointHistoryEntry）按
    // （层, 类别, 类别内序号）登记，供地图节点悬浮联动。同一序号可能对应
    // 多行——问号房打出的战斗同时出现在问号与怪物分组；悬浮时一并高亮。
    private static readonly Dictionary<(int Act, string Category, int SeqIdx), List<NMapPointHistoryEntry>>
        PreviewRowsBySequence = new();

    private const string MapNodeHoverHookedMeta = "ReturnByDeathPreviewHoverHooked";

    public static void Clear()
    {
        if (_container is null || !GodotObject.IsInstanceValid(_container))
            return;
        _container.Visible = false;
        ClearRowHoverTips();
        PreviewRowsBySequence.Clear();
        foreach (var child in _container.GetChildren().ToArray())
            child.QueueFree();
    }

    // 清掉所有预告行仍挂着的悬浮提示与高亮。悬浮地图节点点击出发时
    // MouseExited 不会触发（节点被直接切走），残留的悬浮提示会跟随随后
    // 被释放的预告行并逐帧更新，导致主线程冻结（进火堆卡死的根因）。
    public static void ClearRowHoverTips()
    {
        foreach (var rows in PreviewRowsBySequence.Values)
        {
            foreach (var row in rows)
            {
                if (!GodotObject.IsInstanceValid(row))
                    continue;
                try { row.Unhighlight(); } catch { }
                InvokeRowHoverTip(row, focused: false);
            }
        }
    }

    public static void Refresh()
    {
        // 失忆时间线：遭遇预告隐藏。
        if (AmnesiaState.TimelineHidden)
        {
            Clear();
            return;
        }

        if (!EncounterJournalStore.ReplayActive) return;
        var run = NRun.Instance;
        if (run is null) return;
        var state = RunManager.Instance?.DebugOnlyGetState();
        var livePlayer = state?.Players.FirstOrDefault();
        if (state is null || livePlayer is null) return;

        if (_container is null || !GodotObject.IsInstanceValid(_container) || _container.GetParent() != run)
            _container = CreateContainer(run);
        if (_container is null) return;
        ClearRowHoverTips();
        foreach (var child in _container.GetChildren().ToArray())
            child.QueueFree();
        PreviewRowsBySequence.Clear();

        var memories = BuildMergedMemories();
        var act = state.CurrentActIndex;
        CreateCategory(SelectEntries(memories, act, CategoryNormal), state, livePlayer);
        CreateCategory(SelectEntries(memories, act, CategoryElite), state, livePlayer);
        CreateCategory(SelectEntries(memories, act, CategoryUnknown), state, livePlayer);
        CreateCategory(SelectEntries(memories, act, CategoryShop), state, livePlayer);
        _container.Visible = true;
        ModLog.Write($"Encounter journal shown after sequence merge: act={act}, entries={memories.Count}.");
    }

    private static void CreateCategory(
        List<MergedMemoryEntry> entries, RunState state, Player livePlayer)
    {
        if (_container is null || entries.Count == 0)
            return;

        var history = BuildPreviewHistory(
            entries.Select(memory => memory.Entry).ToList(), state, livePlayer);
        // 分组标题不再显示：照常传入标题 LocString（走原版创建流程），
        // 创建后隐藏标题标签——HBox 布局里标题列随之塌缩，条目整体左移。
        var actEntry = NActHistoryEntry.Create(
            new LocString("return-by-death", "preview-normal-title"),
            history, history.MapPointHistory[0], 1);
        if (actEntry is null)
            return;
        actEntry.GetNodeOrNull<Control>("%Title")?.Visible = false;
        _container.AddChild(actEntry);
        actEntry.SetPlayer(new RunHistoryPlayer
        {
            Id = livePlayer.NetId,
            Character = livePlayer.Character.Id
        });

        // 行序与条目序一致：登记每行对应的（层, 类别, 类别内序号），
        // 供地图节点悬浮 → 预告行高亮的联动使用。同一序号在多个分组各有
        // 一行时全部登记，悬浮地图节点时一并高亮。
        var rows = actEntry.Entries;
        for (var i = 0; i < rows.Count && i < entries.Count; i++)
        {
            var key = (entries[i].Act, entries[i].Category, entries[i].SeqIdx);
            if (!PreviewRowsBySequence.TryGetValue(key, out var bucket))
            {
                bucket = new List<NMapPointHistoryEntry>();
                PreviewRowsBySequence[key] = bucket;
            }
            bucket.Add(rows[i]);
        }
    }

    private sealed class MergedMemoryEntry
    {
        public int Act { get; init; }
        public string Category { get; init; } = CategoryNormal;
        // 类别内序号：该条目是本层此类别（战斗/问号/…）序列中的第几项。
        // 战斗与问号类型的序列各自固定（每进一个同类房间消耗序列下一项），
        // 因此它是路线无关的稳定身份，用于地图节点悬浮联动。
        public int SeqIdx { get; init; }
        public MapPointHistoryEntry Entry { get; set; } = null!;
    }

    internal const string CategoryNormal = "normal";
    internal const string CategoryElite = "elite";
    internal const string CategoryUnknown = "unknown";
    internal const string CategoryShop = "shop";

    // 每条 Episode 都是“出生→本次死亡”的完整历史。合并按（层, 类别, 类别内
    // 序号）进行，同序号冲突时保留较新记忆（“最新”原则）：路线变化只改变
    // 战斗与问号的交错，不改变各自的序列，预告因此始终与地图节点对得上。
    private static List<MergedMemoryEntry> BuildMergedMemories()
    {
        var merged = new Dictionary<(int Act, string Category, int SeqIdx), MergedMemoryEntry>();
        for (var i = 0; i < AmnesiaState.GetEpisodeCount(); i++)
        {
            var episode = AmnesiaState.GetEpisode(i);
            if (episode is null)
                continue;
            CollectEntries(merged, episode.HistoryEntries);
        }

        return merged.Values
            .OrderBy(memory => memory.Act)
            .ThenBy(memory => memory.SeqIdx)
            .ToList();
    }

    private static void CollectEntries(
        Dictionary<(int Act, string Category, int SeqIdx), MergedMemoryEntry> merged,
        IReadOnlyList<IReadOnlyList<MapPointHistoryEntry>> acts)
    {
        for (var act = 0; act < acts.Count; act++)
        {
            // 每层每类别独立计数：本条命第 N 次进入的战斗/问号即序列第 N 项。
            var counters = new Dictionary<string, int>();
            foreach (var entry in acts[act])
            {
                foreach (var category in GetEntryCategories(entry))
                {
                    counters.TryGetValue(category, out var seq);
                    counters[category] = seq + 1;
                    merged[(act, category, seq + 1)] = new MergedMemoryEntry
                    {
                        Act = act,
                        Category = category,
                        SeqIdx = seq + 1,
                        Entry = entry
                    };
                }
            }
        }
    }

    private static List<MergedMemoryEntry> SelectEntries(
        IReadOnlyList<MergedMemoryEntry> merged, int act, string category)
    {
        return merged
            .Where(memory => memory.Act == act && memory.Category == category)
            .OrderBy(memory => memory.SeqIdx)
            .ToList();
    }

    // 一条历史条目所属的类别：问号房打出的战斗/精英同时归入对应怪物类别
    // （它既占问号序列，也占对应怪物的遭遇池序列）。
    private static List<string> GetEntryCategories(MapPointHistoryEntry entry)
    {
        var result = new List<string>();
        if (entry.MapPointType == MapPointType.Monster ||
            (entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Monster)))
            result.Add(CategoryNormal);
        if (entry.MapPointType == MapPointType.Elite ||
            (entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Elite)))
            result.Add(CategoryElite);
        if (entry.MapPointType == MapPointType.Unknown)
            result.Add(CategoryUnknown);
        if (entry.MapPointType == MapPointType.Shop)
            result.Add(CategoryShop);
        return result;
    }

    private static List<string> GetPointCategories(MapPointType pointType) => pointType switch
    {
        MapPointType.Monster => new List<string> { CategoryNormal },
        MapPointType.Elite => new List<string> { CategoryElite },
        MapPointType.Unknown => new List<string> { CategoryUnknown },
        MapPointType.Shop => new List<string> { CategoryShop },
        _ => new List<string>()
    };

    // 把预告条目拼成原版历史记录需要的最小数据：当前层一个分组。
    private static RunHistory BuildPreviewHistory(
        List<MapPointHistoryEntry> entries, RunState state, Player livePlayer)
    {
        return new RunHistory
        {
            Acts = { state.Act.Id },
            MapPointHistory = { entries },
            Players =
            {
                new RunHistoryPlayer
                {
                    Id = livePlayer.NetId,
                    Character = livePlayer.Character.Id
                }
            },
            Win = false
        };
    }

    private static VBoxContainer? CreateContainer(NRun run)
    {
        var container = new VBoxContainer
        {
            Name = "DeathlessEncounterPreview",
            // The left side is normally used for multiplayer party health;
            // single-player recovery runs leave it free for this journal.
            // Move down one UI row so the journal does not cover the second
            // row of relics in the left-side single-player area.
            Position = new Vector2(14, 205),
            Size = new Vector2(420, 640),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 100
        };
        run.AddChild(container);
        return container;
    }

    // 给当前地图的所有节点挂悬浮钩子（每次 SetMap 重建后调用一次）。
    public static void HookMapNodes(NMapScreen screen)
    {
        try
        {
            HookMapNodesRecursive(screen);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Preview map node hover hook failed: {exception.Message}");
        }
    }

    private static void HookMapNodesRecursive(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is NMapPoint point && !point.HasMeta(MapNodeHoverHookedMeta))
            {
                point.SetMeta(MapNodeHoverHookedMeta, true);
                point.MouseEntered += () => SetPreviewRowHighlighted(point, true);
                point.MouseExited += () => SetPreviewRowHighlighted(point, false);
            }
            HookMapNodesRecursive(child);
        }
    }

    private static void SetPreviewRowHighlighted(NMapPoint point, bool highlighted)
    {
        if (!EncounterJournalStore.ReplayActive)
            return;
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null)
            return;

        var act = state.CurrentActIndex;
        var history = act < state.MapPointHistory.Count ? state.MapPointHistory[act] : null;
        if (history is null)
            return;

        var coord = point.Point.coord;
        var visited = state.VisitedMapCoords.Any(v => v.col == coord.col && v.row == coord.row);

        // 类别判定：已到访的节点按它实际发生的内容（问号房打出战斗时两个
        // 类别都算）；未到访的按地图点类型（问号房未来会掷出什么都无法
        // 预知，只归入问号序列）。
        MapPointHistoryEntry? visitedEntry =
            visited && coord.row < history.Count ? history[coord.row] : null;
        var categories = visitedEntry is not null
            ? GetEntryCategories(visitedEntry)
            : GetPointCategories(point.Point.PointType);

        var firstRowShown = false;
        foreach (var category in categories)
        {
            var seqIdx = ResolveSequenceIndex(state, history, point.Point, category, visited);
            if (!seqIdx.HasValue)
                continue;
            if (!PreviewRowsBySequence.TryGetValue((act, category, seqIdx.Value), out var rows))
                continue;

            foreach (var previewRow in rows)
            {
                if (!GodotObject.IsInstanceValid(previewRow))
                    continue;
                if (highlighted)
                    previewRow.Highlight();
                else
                {
                    previewRow.Unhighlight();
                    // 移除本行可能挂着的悬浮提示（幂等）。
                    InvokeRowHoverTip(previewRow, false);
                }
            }

            // 详细信息：与直接把鼠标放到预告行上完全一致——调用该行自己的
            // OnFocus，由原版创建地图点历史悬浮提示（含遭遇详细内容）。
            // 多行同时匹配时只显示第一行的提示，避免互相叠盖。
            if (highlighted && !firstRowShown)
            {
                var firstRow = rows.FirstOrDefault(row => GodotObject.IsInstanceValid(row));
                if (firstRow is not null)
                {
                    InvokeRowHoverTip(firstRow, true);
                    firstRowShown = true;
                }
            }
        }
    }

    private static void InvokeRowHoverTip(NMapPointHistoryEntry row, bool focused)
    {
        try
        {
            AccessTools.Method(typeof(NMapPointHistoryEntry), focused ? "OnFocus" : "OnUnfocus")
                ?.Invoke(row, null);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Preview row hover tip failed: {exception.Message}");
        }
    }

    // 计算地图节点在其类别序列中的序号。已到访：原生历史 0..row 中同类别的
    // 条目数（历史随真实路线走）。未到访：沿地图父节点链向上找最近的已到访
    // 祖先，序号 = 祖先处已消耗的同类别数 + 途中（含本节点）的同类别节点数
    // ——序号跟着节点所在的分支走，切路线不会互相串行。多父分叉取列序靠前
    // 的一条（近似）；找不到已到访祖先时按整条历史已消耗数兜底。
    private static int? ResolveSequenceIndex(
        RunState state,
        IReadOnlyList<MapPointHistoryEntry> history,
        MapPoint node,
        string category,
        bool visited)
    {
        if (visited)
        {
            var row = node.coord.row;
            var visitedCount = 0;
            var last = Math.Min(row, history.Count - 1);
            for (var i = 0; i <= last; i++)
                if (GetEntryCategories(history[i]).Contains(category))
                    visitedCount++;
            return visitedCount > 0 ? visitedCount : null;
        }

        var extra = 0;
        var current = node;
        MapPoint? anchor = null;
        var guard = 0;
        while (current is not null && guard++ < 200)
        {
            if (state.VisitedMapCoords.Any(v => v.col == current.coord.col && v.row == current.coord.row))
            {
                anchor = current;
                break;
            }
            if (GetPointCategories(current.PointType).Contains(category))
                extra++;
            current = current.parents.OrderBy(parent => parent.coord.col).FirstOrDefault();
        }

        var consumed = 0;
        if (anchor is not null)
        {
            var last = Math.Min(anchor.coord.row, history.Count - 1);
            for (var i = 0; i <= last; i++)
                if (GetEntryCategories(history[i]).Contains(category))
                    consumed++;
        }
        else
        {
            foreach (var entry in history)
                if (GetEntryCategories(entry).Contains(category))
                    consumed++;
        }

        return consumed + extra;
    }
}

// 遭遇预告的悬浮提示（原版 NHoverTipSet，挂在 NGame.HoverTipsContainer 下）
// 会被预告条目（ZIndex=100）盖住。实测单纯提高 ZIndex 不可靠——悬浮容器与
// 预告可能分属不同 CanvasLayer（参考变牌特效同类问题的结论）。预告展示期间
// 把新悬浮提示搬进原版顶层 VFX 容器（保留全局坐标，跟随悬浮源的 _Process
// 定位不受影响），这是无条件最上层；预告不展示时不干预原版图层。
// 两个入口都要处理：地图点历史走 CreateAndShowMapPointHistory，
// 遗物/卡牌等走通用 CreateAndShow（单条目重载内部委托给列表重载）。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet),
    nameof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.CreateAndShowMapPointHistory),
    new[] { typeof(Control), typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NMapPointHistoryHoverTip) })]
internal static class PreviewHoverTipLayerPatchMapPoint
{
    [HarmonyPostfix]
    private static void Postfix(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet __result)
    {
        PreviewHoverTipLayer.RaiseAbovePreview(__result);
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet),
    nameof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.CreateAndShow),
    new[] { typeof(Control), typeof(IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>),
        typeof(MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment) })]
internal static class PreviewHoverTipLayerPatchGeneral
{
    [HarmonyPostfix]
    private static void Postfix(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet __result)
    {
        PreviewHoverTipLayer.RaiseAbovePreview(__result);
    }
}

// 出发前往新地图点时强制清理预告行残留的悬浮提示（见 ClearRowHoverTips
// 的注释：悬浮中直接点击节点，MouseExited 不会触发）。
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
internal static class PreviewRowTipTravelCleanupPatch
{
    [HarmonyPrefix]
    private static void Prefix() => EncounterPreviewOverlay.ClearRowHoverTips();
}

internal static class PreviewHoverTipLayer
{
    public static void RaiseAbovePreview(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet? tip)
    {
        if (tip is null || !EncounterJournalStore.ReplayActive)
            return;

        tip.ZIndex = 200;
        var aboveTopBarVfx = NRun.Instance?.GlobalUi?.AboveTopBarVfxContainer;
        if (aboveTopBarVfx is not null && !aboveTopBarVfx.IsAncestorOf(tip))
        {
            // 保留全局坐标：悬浮提示每帧按悬浮源的全局位置重新定位，换父不
            // 影响跟随；关闭时原版按实例 QueueFree，同样不受换父影响。
            tip.Reparent(aboveTopBarVfx, true);
        }
    }
}

// 地图节点悬浮 → 预告行高亮的联动：与原生历史记录里“悬浮卡牌高亮来源
// 节点”互为反向。悬浮地图节点时，若该坐标在遭遇预告里有对应行，就调用
// 原版 NMapPointHistoryEntry.Highlight()（节点图放大 1.5 倍并显示白边）。
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class PreviewMapNodeHoverHookPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) =>
        EncounterPreviewOverlay.HookMapNodes(__instance);
}
