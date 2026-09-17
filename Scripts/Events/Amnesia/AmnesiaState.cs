// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class AmnesiaState
{
    private sealed class MemoryEpisode
    {
        public string Key { get; set; } = string.Empty;
        public List<string> ActIds { get; set; } = new();
        public List<List<MapPointHistoryEntry>> HistoryEntries { get; set; } = new();
        public List<SerializableCard> DeathDeck { get; set; } = new();
        public string CharacterId { get; set; } = string.Empty;
        // 本局的 Unix 开始时间。旧记录没有这个字段时从 Key 的第一段恢复。
        public long RunStartTime { get; set; }
        public long CheckpointNativeSeconds { get; set; }
        public long DeathNativeSeconds { get; set; }
        public long DeathPlaytimeSeconds { get; set; }
    }

    internal sealed class MemoryEpisodeView
    {
        public List<string> ActIds { get; init; } = new();
        public List<List<MapPointHistoryEntry>> HistoryEntries { get; init; } = new();
        public List<SerializableCard> DeathDeck { get; init; } = new();
        public string CharacterId { get; init; } = string.Empty;
        public long RunStartTime { get; init; }
        public long CheckpointNativeSeconds { get; init; }
        public long DeathNativeSeconds { get; init; }
        public long DeathPlaytimeSeconds { get; init; }
    }

    private sealed class AmnesiaFile
    {
        public bool Act3AmnesiaUsed { get; set; }
        public bool TimelineHidden { get; set; }
        public bool RecoveryPending { get; set; }
        public List<SerializableCard> InitialDeck { get; set; } = new();
        public List<SerializableCard> DeathDeck { get; set; } = new();
        public long DeathNativeSeconds { get; set; }
        public long DeathPlaytimeSeconds { get; set; }
        public List<string> HistoryActIds { get; set; } = new();
        public List<List<MapPointHistoryEntry>> HistoryEntries { get; set; } = new();
        // 普通死亡回归也记录为独立片段；旧存档没有此字段时自动得到空列表。
        public List<MemoryEpisode> Episodes { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static readonly string FilePath = ModLog.StateFile("return-by-death.amnesia.json");
    private static AmnesiaFile? _file;

    private static AmnesiaFile EnsureLoaded()
    {
        if (_file is not null)
            return _file;

        try
        {
            _file = JsonSerializer.Deserialize<AmnesiaFile>(File.ReadAllText(FilePath)) ?? new AmnesiaFile();
        }
        catch
        {
            _file = new AmnesiaFile();
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
            ModLog.Write($"Amnesia state save failed: {exception.Message}");
        }
    }

    public static void ResetForNewRun()
    {
        lock (Sync)
        {
            _file = new AmnesiaFile();
            try { File.Delete(FilePath); } catch { }
        }
    }

    // 进入第一层时（先古选项之前）抓取初始卡组，供失忆回归重置使用。
    public static void CaptureInitialDeck(RunState state)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (file.InitialDeck.Count > 0)
                return;

            var player = state.Players.FirstOrDefault();
            if (player is null)
                return;

            file.InitialDeck = player.Deck.Cards.Select(card => card.ToSerializable()).ToList();
            Save();
            ModLog.Write($"Initial deck captured for the amnesia event: {file.InitialDeck.Count} cards.");
        }
    }

    // 第三层第一次死亡：武装一次失忆回归，并保存死亡时的卡组与总时间。
    public static void TryMarkAct3Death(RunState state)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            // 傲慢 IF 线上失忆事件不触发，也不消耗一次性机会。
            if (RouteState.IsIfRoute)
            {
                ModLog.Write("Amnesia event skipped because the run is on the pride IF route.");
                return;
            }
            if (file.Act3AmnesiaUsed || state.CurrentActIndex != 2)
                return;

            file.Act3AmnesiaUsed = true;
            file.RecoveryPending = true;
            var player = state.Players.FirstOrDefault();
            file.DeathDeck = player?.Deck.Cards.Select(card => card.ToSerializable()).ToList()
                ?? new List<SerializableCard>();
            file.DeathNativeSeconds = RunManager.Instance.RunTime;
            file.DeathPlaytimeSeconds = RunManager.Instance.RunTime + TruePlaytimeTracker.ExtraSeconds;
            Save();
            ModLog.Write("Act 3 first death: amnesia recovery armed.");
        }
    }

    public static bool ConsumeRecoveryPending()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            var pending = file.RecoveryPending;
            file.RecoveryPending = false;
            Save();
            return pending;
        }
    }

    // 在检查点被死亡状态覆盖前，保存一次完整的生命结算记录：出生→最新
    // 存档点（检查点里固定的历史）+ 最新存档点→死亡（检查点之后完成的
    // 节点）。每次死亡只写入一条，因而同一局多次死亡会在面板中形成多条
    // 完整记录。
    public static void CaptureDeathEpisode(SerializableRun checkpoint, SerializableRun deathState)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            var key = BuildEpisodeKey(deathState);
            if (file.Episodes.Any(episode => string.Equals(episode.Key, key, StringComparison.Ordinal)))
                return;

            var player = deathState.Players.FirstOrDefault();
            var entries = BuildEpisodeHistory(checkpoint, deathState);
            var episode = new MemoryEpisode
            {
                Key = key,
                ActIds = deathState.Acts
                    .Select(act => act.Id?.ToString() ?? string.Empty)
                    .Where(id => id.Length > 0)
                    .ToList(),
                HistoryEntries = entries,
                DeathDeck = player?.Deck?.ToList() ?? new List<SerializableCard>(),
                CharacterId = player?.CharacterId?.ToString() ?? string.Empty,
                RunStartTime = deathState.StartTime,
                CheckpointNativeSeconds = checkpoint.RunTime,
                DeathNativeSeconds = deathState.RunTime,
                DeathPlaytimeSeconds = deathState.RunTime + TruePlaytimeTracker.ExtraSeconds
            };
            file.Episodes.Add(episode);
            Save();
            ModLog.Write($"Memory episode captured: #{file.Episodes.Count}, " +
                $"act={deathState.CurrentActIndex}, entries={entries.Sum(e => e.Count)}.");
        }
    }

    private static string BuildEpisodeKey(SerializableRun deathState) =>
        $"{deathState.StartTime}:{deathState.RunTime}:{deathState.CurrentActIndex}:" +
        $"{deathState.MapPointHistory.Sum(entries => entries.Count)}";

    private static List<List<MapPointHistoryEntry>> BuildEpisodeHistory(
        SerializableRun checkpoint,
        SerializableRun deathState)
    {
        var result = new List<List<MapPointHistoryEntry>>();
        var actCount = Math.Max(checkpoint.MapPointHistory.Count, deathState.MapPointHistory.Count);
        for (var act = 0; act < actCount; act++)
        {
            var checkpointEntries = act < checkpoint.MapPointHistory.Count
                ? checkpoint.MapPointHistory[act]
                : new List<MapPointHistoryEntry>();
            var deathEntries = act < deathState.MapPointHistory.Count
                ? deathState.MapPointHistory[act]
                : new List<MapPointHistoryEntry>();

            // 出生→最新存档点：检查点里保存的历史原样保留（检查点所在层
            // 之前的各层历史全部在内）。检查点所在层中，死亡状态的历史以
            // 检查点历史为前缀（回归即从该检查点恢复），因此直接取检查点
            // 全部条目，再接上检查点之后完成的节点，即得完整的一条命。
            var segment = new List<MapPointHistoryEntry>();
            segment.AddRange(checkpointEntries);

            var prefix = Math.Min(checkpointEntries.Count, deathEntries.Count);
            var appended = deathEntries.Skip(prefix).ToList();
            // 回归落地时原版 LoadRun 会为存档点所在房间再追加一条历史条目，
            // 其后所有条目相对真实地图坐标行整体后移一位。落地条目与存档点
            // 最后一个节点是同一房间（内容一致），检测到就跳过，保持层内
            // 下标与坐标行对齐——遭遇预告的地图联动依赖这一点。
            if (checkpointEntries.Count > 0 && appended.Count > 0 &&
                IsSameRoomContent(checkpointEntries[^1], appended[0]))
                appended.RemoveAt(0);
            segment.AddRange(appended);
            result.Add(segment);
        }

        return result;
    }

    // 比较两条历史条目是否为同一房间：地图点类型与全部房间内容一致。
    private static bool IsSameRoomContent(MapPointHistoryEntry a, MapPointHistoryEntry b)
    {
        if (a.MapPointType != b.MapPointType || a.Rooms.Count != b.Rooms.Count)
            return false;

        for (var i = 0; i < a.Rooms.Count; i++)
        {
            var ra = a.Rooms[i];
            var rb = b.Rooms[i];
            if (ra.RoomType != rb.RoomType ||
                !string.Equals(ra.ModelId?.ToString(), rb.ModelId?.ToString(), StringComparison.Ordinal) ||
                ra.MonsterIds.Count != rb.MonsterIds.Count)
                return false;
            for (var m = 0; m < ra.MonsterIds.Count; m++)
                if (!string.Equals(ra.MonsterIds[m].ToString(), rb.MonsterIds[m].ToString(), StringComparison.Ordinal))
                    return false;
        }

        return true;
    }

    public static int GetEpisodeCount()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            // 兼容旧版已经保存过的失忆快照：没有 Episodes 字段时仍作为一条
            // 历史记录展示，而不是让面板看起来完全为空。
            return file.Episodes.Count > 0 || file.HistoryEntries.Count > 0
                ? Math.Max(1, file.Episodes.Count)
                : 0;
        }
    }

    // 最近一次死亡的那条命是否胜利过至少一场战斗：每条记忆是一条完整的
    // 生命结算记录（出生→存档点→死亡），最后一个节点是死亡所在节点（未
    // 完成），它之前的战斗节点（普通/精英/Boss）都是打赢了的。代理开关的
    // 展示条件用它判断——遭遇日志会随回归按层清空重建，不能作为依据。
    public static bool LastLifeWonABattle()
    {
        lock (Sync)
        {
            var count = GetEpisodeCount();
            if (count <= 0)
                return false;
            var episode = GetEpisode(count - 1);
            var entries = episode?.HistoryEntries.SelectMany(act => act).ToList();
            if (entries is null)
                return false;
            for (var i = 0; i < entries.Count - 1; i++)
            {
                if (entries[i].Rooms.Any(room =>
                        room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss))
                    return true;
            }
            return false;
        }
    }

    // 代理模式是本局累计解锁：只要任意一条已经结束的生命曾经完成过战斗，
    // 后续回归都继续显示开关。每条死亡记录的最后一个节点是死亡所在的未完成
    // 节点，因此逐条忽略最后一项，再查找普通/精英/Boss 战斗。
    public static bool HasWonABattleInAnyRecordedLife()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (file.Episodes.Count == 0)
            {
                if (file.HistoryEntries.Count == 0)
                    return false;

                return HasCompletedBattleBeforeDeath(
                    file.HistoryEntries.SelectMany(act => act));
            }

            return file.Episodes.Any(episode =>
                HasCompletedBattleBeforeDeath(
                    episode.HistoryEntries.SelectMany(act => act)));
        }
    }

    private static bool HasCompletedBattleBeforeDeath(
        IEnumerable<MapPointHistoryEntry> historyEntries)
    {
        var entries = historyEntries.ToList();
        return entries.Take(Math.Max(0, entries.Count - 1)).Any(entry =>
            entry.Rooms.Any(room =>
                room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss));
    }

    public static MemoryEpisodeView? GetEpisode(int index)
    {
        lock (Sync)
        {
            var episodes = EnsureLoaded().Episodes;
            if (episodes.Count == 0)
            {
                var file = EnsureLoaded();
                if (index != 0 || file.HistoryEntries.Count == 0)
                    return null;

                return new MemoryEpisodeView
                {
                    ActIds = file.HistoryActIds.ToList(),
                    HistoryEntries = file.HistoryEntries.Select(entries => entries.ToList()).ToList(),
                    DeathDeck = file.DeathDeck.ToList(),
                    DeathNativeSeconds = file.DeathNativeSeconds,
                    DeathPlaytimeSeconds = file.DeathPlaytimeSeconds
                };
            }

            if (index < 0 || index >= episodes.Count)
                return null;

            var episode = episodes[index];
            var runStartTime = episode.RunStartTime > 0
                ? episode.RunStartTime
                : ParseRunStartTime(episode.Key);
            return new MemoryEpisodeView
            {
                ActIds = episode.ActIds.ToList(),
                HistoryEntries = episode.HistoryEntries
                    .Select(entries => entries.ToList())
                    .ToList(),
                DeathDeck = episode.DeathDeck.ToList(),
                CharacterId = episode.CharacterId,
                RunStartTime = runStartTime,
                CheckpointNativeSeconds = episode.CheckpointNativeSeconds,
                DeathNativeSeconds = episode.DeathNativeSeconds,
                DeathPlaytimeSeconds = episode.DeathPlaytimeSeconds
            };
        }
    }

    private static long ParseRunStartTime(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return 0;

        var firstPart = key.Split(':', 2)[0];
        return long.TryParse(
            firstPart,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
    }

    // 兼容本机制加入前已经保存的记忆片段：从历史面板里的房间记录
    // 反查该遭遇曾经记录过的 DamageTaken，作为代理模式的旧数据回退。
    public static int? GetHistoricalEncounterDamage(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId))
            return null;

        lock (Sync)
        {
            var best = int.MaxValue;
            foreach (var episode in EnsureLoaded().Episodes)
            foreach (var actEntries in episode.HistoryEntries)
            foreach (var entry in actEntries)
            {
                var rooms = AccessTools.Property(entry.GetType(), "Rooms")?.GetValue(entry)
                            as System.Collections.IEnumerable;
                var matches = rooms is not null && rooms.Cast<object>().Any(room =>
                {
                    var modelId = AccessTools.Property(room.GetType(), "ModelId")?.GetValue(room)
                                  ?? AccessTools.Field(room.GetType(), "ModelId")?.GetValue(room);
                    return string.Equals(modelId?.ToString(), encounterId, StringComparison.Ordinal);
                });
                if (!matches)
                    continue;

                foreach (var stats in entry.PlayerStats)
                {
                    var damageValue = AccessTools.Property(stats.GetType(), "DamageTaken")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "DamageTaken")?.GetValue(stats);
                    if (damageValue is null)
                        continue;

                    var damage = Convert.ToInt32(damageValue);
                    if (damage < best)
                        best = damage;
                }
            }

            return best == int.MaxValue ? null : best;
        }
    }

    public static int? GetHistoricalEncounterHealing(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId))
            return null;

        lock (Sync)
        {
            var best = 0;
            var found = false;
            foreach (var episode in EnsureLoaded().Episodes)
            foreach (var actEntries in episode.HistoryEntries)
            foreach (var entry in actEntries)
            {
                var rooms = AccessTools.Property(entry.GetType(), "Rooms")?.GetValue(entry)
                            as System.Collections.IEnumerable;
                var matches = rooms is not null && rooms.Cast<object>().Any(room =>
                {
                    var modelId = AccessTools.Property(room.GetType(), "ModelId")?.GetValue(room)
                                  ?? AccessTools.Field(room.GetType(), "ModelId")?.GetValue(room);
                    return string.Equals(modelId?.ToString(), encounterId, StringComparison.Ordinal);
                });
                if (!matches)
                    continue;

                foreach (var stats in entry.PlayerStats)
                {
                    var healedValue = AccessTools.Property(stats.GetType(), "HpHealed")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "HpHealed")?.GetValue(stats);
                    if (healedValue is null)
                        continue;

                    best = Math.Max(best, Convert.ToInt32(healedValue));
                    found = true;
                }
            }

            return found ? best : null;
        }
    }

    public static int? GetHistoricalEncounterHpLoss(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId))
            return null;

        lock (Sync)
        {
            var best = int.MinValue;
            var found = false;
            foreach (var episode in EnsureLoaded().Episodes)
            foreach (var actEntries in episode.HistoryEntries)
            foreach (var entry in actEntries)
            {
                var rooms = AccessTools.Property(entry.GetType(), "Rooms")?.GetValue(entry)
                            as System.Collections.IEnumerable;
                var matches = rooms is not null && rooms.Cast<object>().Any(room =>
                {
                    var modelId = AccessTools.Property(room.GetType(), "ModelId")?.GetValue(room)
                                  ?? AccessTools.Field(room.GetType(), "ModelId")?.GetValue(room);
                    return string.Equals(modelId?.ToString(), encounterId, StringComparison.Ordinal);
                });
                if (!matches)
                    continue;

                foreach (var stats in entry.PlayerStats)
                {
                    var damageValue = AccessTools.Property(stats.GetType(), "DamageTaken")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "DamageTaken")?.GetValue(stats);
                    var healedValue = AccessTools.Property(stats.GetType(), "HpHealed")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "HpHealed")?.GetValue(stats);
                    if (damageValue is null && healedValue is null)
                        continue;

                    var hpLoss = Convert.ToInt32(damageValue ?? 0) - Convert.ToInt32(healedValue ?? 0);
                    best = Math.Max(best, hpLoss);
                    found = true;
                }
            }

            return found ? best : null;
        }
    }

    // 遭遇预告的合并身份是“层 + 普通/精英序号”：每条时间线
    // 分别计数，同序号以最新记忆覆盖旧记忆。代理必须读取这个方法，
    // 而不是另一份按 EncounterId 聚合的统计，否则显示与扣血会错位。
    public static bool TryGetPreviewEncounterHpLoss(
        int act,
        bool elite,
        int sequenceIndex,
        out int hpLoss)
    {
        hpLoss = 0;
        if (act < 0 || sequenceIndex <= 0)
            return false;

        lock (Sync)
        {
            var found = false;
            foreach (var episode in EnsureLoaded().Episodes)
            {
                if (act >= episode.HistoryEntries.Count)
                    continue;

                var currentSequence = 0;
                foreach (var entry in episode.HistoryEntries[act])
                {
                    var belongsToCategory = elite
                        ? entry.MapPointType == MapPointType.Elite ||
                          entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Elite)
                        : entry.MapPointType == MapPointType.Monster ||
                          entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Monster);
                    if (!belongsToCategory || ++currentSequence != sequenceIndex)
                        continue;

                    var stats = entry.PlayerStats.FirstOrDefault();
                    if (stats is null)
                        break;

                    var damageValue = AccessTools.Property(stats.GetType(), "DamageTaken")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "DamageTaken")?.GetValue(stats);
                    var healedValue = AccessTools.Property(stats.GetType(), "HpHealed")?.GetValue(stats)
                                      ?? AccessTools.Field(stats.GetType(), "HpHealed")?.GetValue(stats);
                    hpLoss = Convert.ToInt32(damageValue ?? 0) - Convert.ToInt32(healedValue ?? 0);
                    found = true;
                    break;
                }
            }

            return found;
        }
    }

    // 判断预告中的某个“层 + 普通/精英序号”是否曾经真正完成过。
    // 每条死亡记忆的全局最后一个节点是死亡所在的未完成节点；同一序号
    // 只要在任意一条生命中不是最后节点，就说明它至少被打赢过一次。
    public static bool HasCompletedPreviewEncounter(
        int act,
        bool elite,
        int sequenceIndex)
    {
        if (act < 0 || sequenceIndex <= 0)
            return false;

        lock (Sync)
        {
            var file = EnsureLoaded();
            if (file.Episodes.Count == 0)
                return HasCompletedEncounterInHistory(
                    file.HistoryEntries, act, elite, sequenceIndex);

            return file.Episodes.Any(episode =>
                HasCompletedEncounterInHistory(
                    episode.HistoryEntries, act, elite, sequenceIndex));
        }
    }

    private static bool HasCompletedEncounterInHistory(
        IReadOnlyList<List<MapPointHistoryEntry>> history,
        int act,
        bool elite,
        int sequenceIndex)
    {
        if (act >= history.Count)
            return false;

        var lastAct = -1;
        var lastEntryIndex = -1;
        for (var actIndex = history.Count - 1; actIndex >= 0; actIndex--)
        {
            if (history[actIndex].Count == 0)
                continue;
            lastAct = actIndex;
            lastEntryIndex = history[actIndex].Count - 1;
            break;
        }

        var currentSequence = 0;
        for (var entryIndex = 0; entryIndex < history[act].Count; entryIndex++)
        {
            var entry = history[act][entryIndex];
            var belongsToCategory = elite
                ? entry.MapPointType == MapPointType.Elite ||
                  entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Elite)
                : entry.MapPointType == MapPointType.Monster ||
                  entry.MapPointType == MapPointType.Unknown && entry.HasRoomOfType(RoomType.Monster);
            if (!belongsToCategory || ++currentSequence != sequenceIndex)
                continue;

            return act != lastAct || entryIndex != lastEntryIndex;
        }

        return false;
    }

    // 失忆回归时保存“存档前”的作战记录（检查点里的逐层地图历史）与层名，
    // 供记忆面板按原版历史记录的样式显示。
    public static void CaptureHistorySnapshot(SerializableRun checkpoint)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.HistoryActIds = checkpoint.Acts
                .Select(act => act.Id?.ToString() ?? string.Empty)
                .Where(id => id.Length > 0)
                .ToList();
            file.HistoryEntries = checkpoint.MapPointHistory
                .Select(actEntries => actEntries.ToList())
                .ToList();
            Save();
            ModLog.Write($"History snapshot captured: {file.HistoryEntries.Sum(e => e.Count)} map point entries.");
        }
    }

    public static List<string> GetHistoryActIds()
    {
        lock (Sync)
        {
            return EnsureLoaded().HistoryActIds.ToList();
        }
    }

    public static List<List<MapPointHistoryEntry>> GetHistoryEntries()
    {
        lock (Sync)
        {
            return EnsureLoaded().HistoryEntries
                .Select(actEntries => actEntries.ToList())
                .ToList();
        }
    }

    public static void SetTimelineHidden(bool hidden)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.TimelineHidden = hidden;
            Save();
        }
    }

    public static bool TimelineHidden
    {
        get
        {
            lock (Sync)
            {
                return EnsureLoaded().TimelineHidden;
            }
        }
    }

    public static List<SerializableCard> GetInitialDeck()
    {
        lock (Sync)
        {
            return EnsureLoaded().InitialDeck.ToList();
        }
    }

    public static List<SerializableCard> GetDeathDeck()
    {
        lock (Sync)
        {
            return EnsureLoaded().DeathDeck.ToList();
        }
    }

    public static long DeathPlaytimeSeconds
    {
        get
        {
            lock (Sync)
            {
                return EnsureLoaded().DeathPlaytimeSeconds;
            }
        }
    }

    public static long DeathNativeSeconds
    {
        get
        {
            lock (Sync)
            {
                return EnsureLoaded().DeathNativeSeconds;
            }
        }
    }
}

// 左上角角色头像按钮 + 死亡记忆面板：点击头像查看每次“最新存档 → 死亡”
// 的独立经历记录；旧版失忆快照仍作为没有普通死亡记录时的兼容回退。
