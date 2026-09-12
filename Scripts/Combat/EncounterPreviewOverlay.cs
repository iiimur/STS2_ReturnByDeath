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
// 的奖励等原生内容。所有记忆按（层, 房间身份, 本局第几次出现）合并，同一
// 事件或遭遇即使在新时间线中的位置发生变化，也由较新的记录覆盖。分组四行：
// 普通怪物、精英怪物、问号、商店；问号房打出的战斗同时留在对应怪物分组。
internal static class EncounterPreviewOverlay
{
    private static VBoxContainer? _container;

    // 预告行（NActHistoryEntry 里的每个 NMapPointHistoryEntry）按地图坐标
    // （层, 到访行）登记，供地图节点悬浮联动。
    private static readonly Dictionary<(int Act, int Row), NMapPointHistoryEntry> PreviewRowsByCoord =
        new();

    private const string MapNodeHoverHookedMeta = "ReturnByDeathPreviewHoverHooked";

    public static void Clear()
    {
        if (_container is null || !GodotObject.IsInstanceValid(_container))
            return;
        _container.Visible = false;
        PreviewRowsByCoord.Clear();
        foreach (var child in _container.GetChildren().ToArray())
            child.QueueFree();
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
        foreach (var child in _container.GetChildren().ToArray())
            child.QueueFree();
        PreviewRowsByCoord.Clear();

        var memories = BuildMergedMemories();
        var act = state.CurrentActIndex;
        CreateCategory("preview-normal-title",
            SelectEntries(memories, act, IsNormalEntry), state, livePlayer);
        CreateCategory("preview-elite-title",
            SelectEntries(memories, act, IsEliteEntry), state, livePlayer);
        CreateCategory("preview-unknown-title",
            SelectEntries(memories, act, IsUnknownEntry), state, livePlayer);
        CreateCategory("preview-shop-title",
            SelectEntries(memories, act, IsShopEntry), state, livePlayer);
        _container.Visible = true;
        ModLog.Write($"Encounter journal shown after identity merge: act={act}, entries={memories.Count}.");
    }

    private static void CreateCategory(
        string titleKey, List<MergedMemoryEntry> entries, RunState state, Player livePlayer)
    {
        if (_container is null || entries.Count == 0)
            return;

        var history = BuildPreviewHistory(
            entries.Select(memory => memory.Entry).ToList(), state, livePlayer);
        var actEntry = NActHistoryEntry.Create(
            new LocString("return-by-death", titleKey), history, history.MapPointHistory[0], 1);
        if (actEntry is null)
            return;
        _container.AddChild(actEntry);
        actEntry.SetPlayer(new RunHistoryPlayer
        {
            Id = livePlayer.NetId,
            Character = livePlayer.Character.Id
        });

        // 行序与条目序一致：登记每行对应的地图坐标（层, 到访行），
        // 供地图节点悬浮 → 预告行高亮的联动使用。
        var rows = actEntry.Entries;
        for (var i = 0; i < rows.Count && i < entries.Count; i++)
            PreviewRowsByCoord[(entries[i].Act, entries[i].Row)] = rows[i];
    }

    private sealed class MergedMemoryEntry
    {
        public int Act { get; init; }
        public int Order { get; init; }
        // 该条目在记忆历史层列表中的下标。原生历史按到访顺序追加且以坐标
        // row 为下标（与原版 GetHistoryEntryFor 一致），因此它就是地图坐标
        // 的行号，用于地图节点悬浮 → 预告行高亮的联动。
        public int Row { get; init; }
        public MapPointHistoryEntry Entry { get; set; } = null!;
    }

    // 每条 Episode 都是“出生→本次死亡”的完整历史。相同内容在不同时间线
    // 可能因为改走路线或跳过节点而落在不同列表下标，所以不能再用到访序号
    // 作为身份。相同事件/遭遇在一条生命中的第 N 次出现视为同一条记录；
    // 后读到的 Episode 更新详情，但保留该记录第一次出现时的排列位置。
    private static List<MergedMemoryEntry> BuildMergedMemories()
    {
        var merged = new Dictionary<(int Act, string Identity, int Occurrence), MergedMemoryEntry>();
        var nextOrder = 0;
        for (var i = 0; i < AmnesiaState.GetEpisodeCount(); i++)
        {
            var episode = AmnesiaState.GetEpisode(i);
            if (episode is null)
                continue;

            CollectEntries(merged, episode.HistoryEntries, ref nextOrder);
        }

        return merged.Values.OrderBy(entry => entry.Order).ToList();
    }

    private static void CollectEntries(
        Dictionary<(int Act, string Identity, int Occurrence), MergedMemoryEntry> merged,
        IReadOnlyList<IReadOnlyList<MapPointHistoryEntry>> acts,
        ref int nextOrder)
    {
        var occurrences = new Dictionary<(int Act, string Identity), int>();
        for (var act = 0; act < acts.Count; act++)
        {
            var actEntries = acts[act];
            for (var row = 0; row < actEntries.Count; row++)
            {
                var entry = actEntries[row];
                var identity = BuildEntryIdentity(entry);
                var occurrenceKey = (act, identity);
                occurrences.TryGetValue(occurrenceKey, out var occurrence);
                occurrences[occurrenceKey] = occurrence + 1;

                var key = (act, identity, occurrence);
                if (merged.TryGetValue(key, out var existing))
                {
                    existing.Entry = entry;
                }
                else
                {
                    merged[key] = new MergedMemoryEntry
                    {
                        Act = act,
                        Order = nextOrder++,
                        Row = row,
                        Entry = entry
                    };
                }
            }
        }
    }

    private static string BuildEntryIdentity(MapPointHistoryEntry entry)
    {
        var rooms = entry.Rooms.Select(room =>
        {
            var modelId = room.ModelId?.ToString() ?? string.Empty;
            var monsters = string.Join(",", room.MonsterIds.Select(id => id.ToString()));
            // 有事件/遭遇 ModelId 时它就是稳定身份；怪物列表可能因版本、
            // 修正器或生成细节改变，不应让同一遭遇变成一条新记录。
            var contentId = modelId.Length > 0 ? modelId : monsters;
            return $"{room.RoomType}:{contentId}";
        });
        return $"{entry.MapPointType}|{string.Join(">", rooms)}";
    }

    private static List<MergedMemoryEntry> SelectEntries(
        IReadOnlyList<MergedMemoryEntry> merged,
        int act,
        Func<MapPointHistoryEntry, bool> include)
    {
        return merged
            .Where(memory => memory.Act == act && include(memory.Entry))
            .OrderBy(memory => memory.Order)
            .ToList();
    }

    // 普通怪物：普通怪地图点，以及问号房里打出的普通战斗（同步展示）。
    private static bool IsNormalEntry(MapPointHistoryEntry entry) =>
        entry.MapPointType == MapPointType.Monster ||
        (entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Monster));

    private static bool IsEliteEntry(MapPointHistoryEntry entry) =>
        entry.MapPointType == MapPointType.Elite ||
        (entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Elite));

    private static bool IsUnknownEntry(MapPointHistoryEntry entry) =>
        entry.MapPointType == MapPointType.Unknown;

    private static bool IsShopEntry(MapPointHistoryEntry entry) =>
        entry.MapPointType == MapPointType.Shop;

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

        var coord = point.Point.coord;
        if (!PreviewRowsByCoord.TryGetValue((state.CurrentActIndex, coord.row), out var row) ||
            !GodotObject.IsInstanceValid(row))
            return;

        // 不在预告中的节点查不到行，自然没有高亮。
        if (highlighted)
            row.Highlight();
        else
            row.Unhighlight();
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
