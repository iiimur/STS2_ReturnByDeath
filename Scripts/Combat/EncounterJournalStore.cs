// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal sealed class EncounterJournalFile
{
    public List<EncounterRecord> Encounters { get; set; } = new();
}

internal sealed class EncounterRecord
{
    public int Act { get; set; }
    public string MapKey { get; set; } = string.Empty;
    public string EncounterId { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public List<EncounterMonsterRecord> Monsters { get; set; } = new();
    // 历史完成该遭遇时实际损失生命的最小值；旧日志没有此字段时为空。
    public int? MinimumDamageTaken { get; set; }
    // 同一遭遇历史上记录到的最大回血值（战士/血瓶等来源）。
    public int? MaximumHpHealed { get; set; }
    // 同一遭遇历史上“战斗前生命 - 战斗后生命”的最大净损失。
    // 正数代表扣血，负数代表这场战斗最终回血更多。
    public int? MaximumHpLoss { get; set; }
    // 最近一次完成该日志遭遇的净损失（每次打完直接覆盖）。
    // 现在代理重放直接读取遭遇预告的同一条记忆；该值仅用于旧数据兼容与诊断。
    public int? LatestHpLoss { get; set; }
    // 只有真正进入过 OfferRoomEndRewards 的战斗才能代理。可空以兼容旧日志；
    // 旧记录可由已存在的生命统计推断为已胜利。
    public bool? Completed { get; set; }
}

internal sealed class EncounterMonsterRecord
{
    public string Id { get; set; } = string.Empty;
    public string Slot { get; set; } = string.Empty;
}

// 遭遇预告不是游戏原生存档的一部分，而是本 mod 自己维护的一份“小型日志”。
// 记录阶段按地图坐标保存；回归阶段按普通/精英序列重放。
internal static class EncounterJournalStore
{
    public static bool ReplayActive => Volatile.Read(ref _replayActive) != 0;
    // 兼容旧调用：只有已经实际进入过原生奖励结算、并且能与当前
    // 遭遇预告序号对应上的战斗，才允许代理跳过。
    public static bool ShouldSkipCurrentEncounter =>
        ReplayActive && Volatile.Read(ref _currentEncounterShouldSkip) != 0;
    private static readonly object Sync = new();
    private static readonly string JournalPath = Path.Combine(RecoveryMarker.ModDirectory, "deathless-run.encounter-journal.json");
    private static readonly string LegacyPath = Path.Combine(RecoveryMarker.ModDirectory, "deathless-run.fixed-rooms.json");
    private static readonly string ReplayModePath = Path.Combine(RecoveryMarker.ModDirectory, "deathless-run.encounter-replay");
    private static EncounterJournalFile? _file;
    private static MapCoord? _pendingCoordinate;
    private static int _replayActive;
    private static int _replayNormalRecordCursor;
    private static int _replayEliteRecordCursor;
    private static int _currentEncounterShouldSkip;
    private static int _currentEncounterStartHp = -1;
    private static int _currentEncounterSkipped;
    private static EncounterRecord? _currentSkippedEncounter;
    private static EncounterRecord? _currentRecordedEncounter;
    // StartCombat 与 OfferRoomEndRewards 之间可能会再次预生成下一场遭遇。
    // 因此不能只依赖“当前生成的遭遇”来结算生命变化；进入战斗时把本场
    // 记录和起始生命锁定下来，结算时始终写回这条记录。
    private static EncounterRecord? _currentCombatEncounter;
    private static int _currentCombatStartHp = -1;
    private static readonly HashSet<int> _seenJournalModels = new();
    private static string? _lastReplayCallSignature;
    private static int _previewAct = -1;

    public static bool ShouldSkipEncounter(IRunState state, EncounterModel encounter)
    {
        if (!ReplayActive || encounter.RoomType is RoomType.Boss ||
            !IsMonsterRoom(encounter.RoomType) || IsStandaloneEventEncounter(encounter))
        {
            return false;
        }

        lock (Sync)
        {
            EnsureLoaded();
            var current = ResolveReplayRecord(state, encounter);

            if (current is null)
                return false;

            // StartCombat 通常紧跟在这里之后，但在某些地图节点上生成顺序
            // 会反过来。先把当前遭遇绑定到这条记录，避免读取上一场战斗。
            _currentRecordedEncounter = current;

            var previewHpLoss = 0;
            var hasPreview = TryGetPreviewHpLoss(current, out previewHpLoss);
            var completed = IsCompletedEncounter(current) ||
                            HasCompletedPreviewEncounter(current);
            var skip = completed && hasPreview;
            ModLog.Write($"Encounter replay decision: act={state.CurrentActIndex}, " +
                $"encounter={encounter.Id}, currentMap={state.CurrentMapCoord?.ToString() ?? "none"}, " +
                $"skip={skip}, completed={completed}, " +
                $"previewHpLoss={(hasPreview ? previewHpLoss.ToString() : "none")}.");
            return skip;
        }
    }

    public static void MarkCurrentEncounterSkipped()
    {
        _currentSkippedEncounter = _currentCombatEncounter ?? _currentRecordedEncounter;
        Volatile.Write(ref _currentEncounterSkipped, 1);
    }

    public static bool CurrentEncounterWasSkipped =>
        Volatile.Read(ref _currentEncounterSkipped) != 0 &&
        ReferenceEquals(_currentSkippedEncounter, _currentCombatEncounter ?? _currentRecordedEncounter);

    public static void BeginCombatTracking(IRunState state, EncounterModel encounter)
    {
        if (!IsMonsterRoom(encounter.RoomType) || encounter.RoomType == RoomType.Boss ||
            IsStandaloneEventEncounter(encounter))
            return;

        lock (Sync)
        {
            EnsureLoaded();
            var encounterId = encounter.Id.ToString();
            // StartCombat 前缀早于 GenerateMonstersWithSlots；此时 CurrentMapCoord
            // 可能已指向新节点，但坐标上保留的旧记录仍是另一场遭遇。
            // 回归期间必须先按实际 EncounterId/序号锁定，不能用坐标把
            // 相邻记录锁成同一个对象。
            var current = ReplayActive
                ? ResolveReplayRecord(state, encounter)
                : ResolveRecordedEncounter(state, encounterId);

            // 第一次时间线中 StartCombat 早于 GenerateMonstersWithSlots；此时
            // 日志还没有当前遭遇对象。立即创建占位记录，奖励结算才能把
            // Completed 与生命变化写回这一场，稍后的怪物生成会补齐怪物列表。
            if (current is null && !ReplayActive && state.CurrentMapCoord is { } coord)
            {
                current = new EncounterRecord
                {
                    Act = state.CurrentActIndex,
                    MapKey = Key(state.CurrentActIndex, coord),
                    EncounterId = encounterId,
                    RoomType = encounter.RoomType.ToString()
                };
                _file!.Encounters.Add(current);
                Save();
            }

            if (current is null)
                return;

            _currentRecordedEncounter = current;
            _currentCombatEncounter = current;
            _currentCombatStartHp = state.Players.FirstOrDefault()?.Creature.CurrentHp ?? -1;
            Volatile.Write(ref _currentEncounterStartHp, _currentCombatStartHp);
            Volatile.Write(ref _currentEncounterSkipped, 0);
            _currentSkippedEncounter = null;
            ModLog.Write($"Encounter combat tracking locked: {current.EncounterId}, " +
                $"start HP={_currentCombatStartHp}.");
        }
    }

    public static int GetCurrentEncounterMinimumDamage()
    {
        lock (Sync)
        {
            var encounter = _currentCombatEncounter ?? _currentRecordedEncounter;
            if (encounter is null)
                return 0;

            return encounter.MinimumDamageTaken ??
                   AmnesiaState.GetHistoricalEncounterDamage(encounter.EncounterId) ??
                   0;
        }
    }

    public static int GetCurrentEncounterMaximumHealing()
    {
        lock (Sync)
        {
            var encounter = _currentCombatEncounter ?? _currentRecordedEncounter;
            if (encounter is null)
                return 0;

            return encounter.MaximumHpHealed ??
                   AmnesiaState.GetHistoricalEncounterHealing(encounter.EncounterId) ??
                   0;
        }
    }

    public static int GetCurrentEncounterHpLoss()
    {
        lock (Sync)
        {
            var encounter = _currentCombatEncounter ?? _currentRecordedEncounter;
            if (encounter is null)
                return 0;

            if (ReplayActive && TryGetPreviewHpLoss(encounter, out var previewHpLoss))
                return previewHpLoss;

            return GetEncounterHpLoss(encounter);
        }
    }

    // 地图点击尚未生成 EncounterModel，因此按 RecordMonsters 将要使用的
    // 分类游标预读遭遇预告。这里只查看，不推进游标；取消弹窗后状态完全不变。
    public static bool TryGetUpcomingProxyHpLoss(
        IRunState state,
        MapCoord coord,
        out int hpLoss)
    {
        hpLoss = 0;
        if (!ReplayActive)
            return false;

        var point = state.Map.GetPoint(coord);
        if (point?.PointType is not (MapPointType.Monster or MapPointType.Elite))
            return false;

        lock (Sync)
        {
            EnsureLoaded();
            var elite = point.PointType == MapPointType.Elite;
            var category = _file!.Encounters
                .Where(record => record.Act == state.CurrentActIndex &&
                                 IsSameCategory(record.RoomType, elite))
                .ToList();
            var cursor = elite ? _replayEliteRecordCursor : _replayNormalRecordCursor;
            if (cursor >= category.Count ||
                !(IsCompletedEncounter(category[cursor]) ||
                  AmnesiaState.HasCompletedPreviewEncounter(
                      state.CurrentActIndex, elite, cursor + 1)) ||
                !AmnesiaState.TryGetPreviewEncounterHpLoss(
                    state.CurrentActIndex, elite, cursor + 1, out hpLoss))
                return false;

            return true;
        }
    }

    // 代理与预告共用同一个“层 + 类别 + 序号”。遭遇日志仅用于
    // 确定当前是序列中的第几项，血量变化直接从记忆面板的合并记录读取。
    private static bool TryGetPreviewHpLoss(EncounterRecord encounter, out int hpLoss)
    {
        hpLoss = 0;
        if (!TryGetPreviewSequence(encounter, out var elite, out var sequenceIndex))
            return false;

        return AmnesiaState.TryGetPreviewEncounterHpLoss(
            encounter.Act, elite, sequenceIndex, out hpLoss);
    }

    private static bool HasCompletedPreviewEncounter(EncounterRecord encounter) =>
        TryGetPreviewSequence(encounter, out var elite, out var sequenceIndex) &&
        AmnesiaState.HasCompletedPreviewEncounter(
            encounter.Act, elite, sequenceIndex);

    private static bool TryGetPreviewSequence(
        EncounterRecord encounter,
        out bool elite,
        out int sequenceIndex)
    {
        elite = false;
        sequenceIndex = 0;
        if (!Enum.TryParse<RoomType>(encounter.RoomType, out var roomType) ||
            roomType is not (RoomType.Monster or RoomType.Elite))
            return false;

        elite = roomType == RoomType.Elite;
        var isElite = elite;
        var category = _file!.Encounters
            .Where(record => record.Act == encounter.Act && IsSameCategory(record.RoomType, isElite))
            .ToList();
        sequenceIndex = category.IndexOf(encounter) + 1;
        return sequenceIndex > 0;
    }

    private static EncounterRecord? ResolveReplayRecord(IRunState state, EncounterModel encounter)
    {
        var elite = encounter.RoomType == RoomType.Elite;
        var encounterId = encounter.Id.ToString();
        if (_currentRecordedEncounter is { } recorded &&
            _seenJournalModels.Contains(RuntimeHelpers.GetHashCode(encounter)) &&
            recorded.Act == state.CurrentActIndex &&
            IsSameCategory(recorded.RoomType, elite) &&
            string.Equals(recorded.EncounterId, encounterId, StringComparison.Ordinal))
        {
            return recorded;
        }

        var category = _file!.Encounters
            .Where(record => record.Act == state.CurrentActIndex && IsSameCategory(record.RoomType, elite))
            .ToList();
        var cursor = elite ? _replayEliteRecordCursor : _replayNormalRecordCursor;
        return category.Skip(Math.Min(cursor, category.Count)).FirstOrDefault(record =>
                   string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal))
               ?? category.FirstOrDefault(record =>
                   string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal));
    }

    private static EncounterRecord? ResolveRecordedEncounter(IRunState state, string encounterId)
    {
        if (state.CurrentMapCoord is { } coord)
        {
            var mapKey = Key(state.CurrentActIndex, coord);
            var byCoordinate = _file!.Encounters.FirstOrDefault(record =>
                record.Act == state.CurrentActIndex &&
                string.Equals(record.MapKey, mapKey, StringComparison.Ordinal) &&
                string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal));
            if (byCoordinate is not null)
                return byCoordinate;
        }

        return _file!.Encounters.LastOrDefault(record =>
            record.Act == state.CurrentActIndex &&
            string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal));
    }

    private static int GetEncounterHpLoss(EncounterRecord encounter)
    {

        // 旧日志兼容回退：优先取最近一次净损失，再退回历史统计。
        if (encounter.LatestHpLoss.HasValue)
            return encounter.LatestHpLoss.Value;

        var historical = AmnesiaState.GetHistoricalEncounterHpLoss(encounter.EncounterId);
        var fallback = (encounter.MinimumDamageTaken ?? 0) -
                       (encounter.MaximumHpHealed ?? 0);
        return new[]
        {
            encounter.MaximumHpLoss,
            historical,
            (int?)fallback
        }.Where(value => value.HasValue)
         .Select(value => value!.Value)
         .Max();
    }

    private static bool IsCompletedEncounter(EncounterRecord encounter) =>
        encounter.Completed == true ||
        // 1.0.8 及更早版本没有 Completed；正常结算过的旧战斗会留下
        // 至少一项生命统计，借此无需重打便可继续代理。
        encounter.LatestHpLoss.HasValue || encounter.MinimumDamageTaken.HasValue ||
        encounter.MaximumHpLoss.HasValue;

    public static void RecordCurrentEncounterDamage()
    {
        if (SlothRouteRules.IsActive || CurrentEncounterWasSkipped)
            return;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var currentHp = state?.Players.FirstOrDefault()?.Creature.CurrentHp;
        var startHp = Volatile.Read(ref _currentCombatStartHp);
        if (state is null || currentHp is null || startHp < 0)
            return;

        var historyStats = ReadCurrentEncounterHealthStats(state);
        var damageTaken = historyStats.DamageTaken ?? Math.Max(0, startHp - currentHp.Value);
        var hpHealed = historyStats.HpHealed ?? 0;
        var hpLoss = startHp - currentHp.Value;
        lock (Sync)
        {
            if (_currentCombatEncounter is null)
                return;

            var changed = false;
            if (_currentCombatEncounter.Completed != true)
            {
                _currentCombatEncounter.Completed = true;
                changed = true;
            }
            if (!_currentCombatEncounter.MinimumDamageTaken.HasValue ||
                damageTaken < _currentCombatEncounter.MinimumDamageTaken.Value)
            {
                _currentCombatEncounter.MinimumDamageTaken = damageTaken;
                changed = true;
            }
            if (!_currentCombatEncounter.MaximumHpHealed.HasValue ||
                hpHealed > _currentCombatEncounter.MaximumHpHealed.Value)
            {
                _currentCombatEncounter.MaximumHpHealed = hpHealed;
                changed = true;
            }
            if (!_currentCombatEncounter.MaximumHpLoss.HasValue ||
                hpLoss > _currentCombatEncounter.MaximumHpLoss.Value)
            {
                _currentCombatEncounter.MaximumHpLoss = hpLoss;
                changed = true;
            }
            if (_currentCombatEncounter.LatestHpLoss != hpLoss)
            {
                _currentCombatEncounter.LatestHpLoss = hpLoss;
                changed = true;
            }
            if (changed)
            {
                Save();
                ModLog.Write($"Encounter health history recorded: {_currentCombatEncounter.EncounterId}, " +
                    $"start HP={startHp}, end HP={currentHp.Value}, " +
                    $"minimum damage={_currentCombatEncounter.MinimumDamageTaken}, " +
                    $"maximum healing={_currentCombatEncounter.MaximumHpHealed}, " +
                    $"maximum net HP loss={_currentCombatEncounter.MaximumHpLoss}, " +
                    $"latest net HP loss={_currentCombatEncounter.LatestHpLoss}.");
            }
        }
    }

    private static (int? DamageTaken, int? HpHealed) ReadCurrentEncounterHealthStats(RunState state)
    {
        try
        {
            var stats = state.CurrentMapPointHistoryEntry?.PlayerStats.FirstOrDefault();
            if (stats is null)
                return (null, null);

            var damage = AccessTools.Property(stats.GetType(), "DamageTaken")?.GetValue(stats)
                         ?? AccessTools.Field(stats.GetType(), "DamageTaken")?.GetValue(stats);
            var healed = AccessTools.Property(stats.GetType(), "HpHealed")?.GetValue(stats)
                         ?? AccessTools.Field(stats.GetType(), "HpHealed")?.GetValue(stats);
            return (
                damage is null ? null : Math.Max(0, Convert.ToInt32(damage)),
                healed is null ? null : Math.Max(0, Convert.ToInt32(healed)));
        }
        catch
        {
            return (null, null);
        }
    }

    public static ModelId? TextToId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var split = text.Split('.', 2);
        return split.Length == 2 ? new ModelId(split[0], split[1]) : null;
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _file = new EncounterJournalFile();
            _pendingCoordinate = null;
            Volatile.Write(ref _replayActive, 0);
            _replayNormalRecordCursor = 0;
            _replayEliteRecordCursor = 0;
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            Volatile.Write(ref _currentEncounterStartHp, -1);
            Volatile.Write(ref _currentEncounterSkipped, 0);
            _currentSkippedEncounter = null;
            _currentRecordedEncounter = null;
            _currentCombatEncounter = null;
            Volatile.Write(ref _currentCombatStartHp, -1);
            _seenJournalModels.Clear();
            _lastReplayCallSignature = null;
            _previewAct = -1;
            try { File.Delete(JournalPath); } catch { }
            try { File.Delete(LegacyPath); } catch { }
            try { File.Delete(ReplayModePath); } catch { }
        }
    }

    public static void RestoreReplayMode()
    {
        try
        {
            if (!File.Exists(ReplayModePath))
                return;

            Volatile.Write(ref _replayActive, 1);
            lock (Sync)
            {
                _replayNormalRecordCursor = 0;
                _replayEliteRecordCursor = 0;
                Volatile.Write(ref _currentEncounterShouldSkip, 0);
                Volatile.Write(ref _currentEncounterStartHp, -1);
                Volatile.Write(ref _currentEncounterSkipped, 0);
                _currentSkippedEncounter = null;
                _currentRecordedEncounter = null;
                _currentCombatEncounter = null;
                Volatile.Write(ref _currentCombatStartHp, -1);
                _seenJournalModels.Clear();
                _lastReplayCallSignature = null;
            }
            ModLog.Write("Persistent encounter replay mode restored after process restart.");
        }
        catch { }
    }

    public static void SetReplayActive(bool active)
    {
        Volatile.Write(ref _replayActive, active ? 1 : 0);
        if (!active)
        {
            try { File.Delete(ReplayModePath); } catch { }
            return;
        }
        try { File.WriteAllText(ReplayModePath, "active"); } catch { }
        lock (Sync)
        {
            _replayNormalRecordCursor = 0;
            _replayEliteRecordCursor = 0;
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            Volatile.Write(ref _currentEncounterStartHp, -1);
            Volatile.Write(ref _currentEncounterSkipped, 0);
            _currentSkippedEncounter = null;
            _currentRecordedEncounter = null;
            _currentCombatEncounter = null;
            Volatile.Write(ref _currentCombatStartHp, -1);
            _seenJournalModels.Clear();
            _lastReplayCallSignature = null;
        }
    }

    public static void StopForArchitect()
    {
        lock (Sync)
        {
            Volatile.Write(ref _replayActive, 0);
            _pendingCoordinate = null;
            _replayNormalRecordCursor = 0;
            _replayEliteRecordCursor = 0;
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            Volatile.Write(ref _currentEncounterStartHp, -1);
            Volatile.Write(ref _currentEncounterSkipped, 0);
            _currentSkippedEncounter = null;
            _currentRecordedEncounter = null;
            _currentCombatEncounter = null;
            Volatile.Write(ref _currentCombatStartHp, -1);
            _seenJournalModels.Clear();
            _lastReplayCallSignature = null;
            try { File.Delete(ReplayModePath); } catch { }
        }
        EncounterPreviewOverlay.Clear();
    }

    public static void SetPreviewAct(int act)
    {
        lock (Sync)
        {
            EnsureLoaded();
            // The forecast is per act. Once the Ancient transition finishes,
            // discard the completed act's sequence so a new encounter cannot
            // match and re-display old entries.
            _file!.Encounters.RemoveAll(x => x.Act != act);
            Save();
            _previewAct = act;
            _replayNormalRecordCursor = 0;
            _replayEliteRecordCursor = 0;
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            Volatile.Write(ref _currentEncounterStartHp, -1);
            Volatile.Write(ref _currentEncounterSkipped, 0);
            _currentSkippedEncounter = null;
            _currentRecordedEncounter = null;
            _currentCombatEncounter = null;
            Volatile.Write(ref _currentCombatStartHp, -1);
            _seenJournalModels.Clear();
            _lastReplayCallSignature = null;
        }
        if (ReplayActive)
            EncounterPreviewOverlay.Refresh();
    }

    public static void ClearPreview()
    {
        lock (Sync)
        {
            // Use a sentinel instead of deleting the journal: the next
            // encounter in the new act will switch this to that act and begin
            // filling the forecast again.
            _previewAct = int.MaxValue;
            _replayNormalRecordCursor = 0;
            _replayEliteRecordCursor = 0;
            _currentRecordedEncounter = null;
            _seenJournalModels.Clear();
        }
        EncounterPreviewOverlay.Clear();
    }

    public static void SetPendingCoordinate(MapCoord coord) => _pendingCoordinate = coord;

    private static void ClearPendingCoordinate() => _pendingCoordinate = null;

    private static bool TryGetCoordinate(IRunState state, out MapCoord coord)
    {
        if (_pendingCoordinate.HasValue)
        {
            coord = _pendingCoordinate.Value;
            return true;
        }
        coord = state.CurrentMapCoord.GetValueOrDefault();
        return state.CurrentMapCoord.HasValue;
    }

    // EncounterModel 生成怪物时调用。事件专属遭遇在这里过滤，避免错误消耗普通池。
    public static void RecordMonsters(IRunState state, EncounterModel encounter)
    {
        if (ArchitectFinaleState.IsActive)
        {
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            return;
        }
        if (IsStandaloneEventEncounter(encounter))
        {
            // Encounters such as the three-HP-choice Battleworn Dummy are
            // generated by a specific question-mark event, not drawn from
            // the normal or elite encounter pools. Do not advance either
            // preview sequence for them.
            ClearPendingCoordinate();
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            ModLog.Write($"Ignored standalone event encounter in preview: {encounter.Id}.");
            return;
        }
        if (!IsMonsterRoom(encounter.RoomType) || encounter.RoomType == RoomType.Boss)
        {
            // Boss 与特殊事件不进入普通遭遇预告，不能继承上一场普通战斗的
            // “可跳过”状态，否则会把 Boss/事件战斗误判为已完成。
            Volatile.Write(ref _currentEncounterShouldSkip, 0);
            return;
        }
        var hasCoord = TryGetCoordinate(state, out var coord);
        if (!ReplayActive && !hasCoord) return;
        lock (Sync)
        {
            EnsureLoaded();
            var monsters = encounter.MonstersWithSlots.Select(x => new EncounterMonsterRecord
            {
                Id = x.Item1.Id.ToString(),
                Slot = x.Item2 ?? string.Empty
            }).ToList();

            if (ReplayActive)
            {
                // GenerateMonstersWithSlots can be reached more than once for
                // the same room while the combat scene is being reconstructed.
                // Do not advance the replay cursor for that duplicate call.
                var mapKey = hasCoord ? Key(state.CurrentActIndex, coord) : string.Empty;
                var callSignature = $"{state.CurrentActIndex}|{encounter.RoomType}|{encounter.Id}|{string.Join(',', monsters.Select(x => $"{x.Id}:{x.Slot}"))}|{mapKey}";
                if (_lastReplayCallSignature == callSignature)
                    return;
                _lastReplayCallSignature = callSignature;

                if (!_seenJournalModels.Add(RuntimeHelpers.GetHashCode(encounter))) return;
                if (_previewAct != state.CurrentActIndex)
                {
                    _file!.Encounters.RemoveAll(x => x.Act != state.CurrentActIndex);
                    _previewAct = state.CurrentActIndex;
                    _replayNormalRecordCursor = 0;
                    _replayEliteRecordCursor = 0;
                    _lastReplayCallSignature = null;
                }
                var elite = encounter.RoomType == RoomType.Elite;
                var category = _file!.Encounters
                    .Where(x => x.Act == state.CurrentActIndex && IsSameCategory(x.RoomType, elite))
                    .ToList();
                var cursor = elite ? _replayEliteRecordCursor++ : _replayNormalRecordCursor++;
                // 回归后地图可能走不同路线，CurrentMapCoord 与历史 MapKey
                // 不再一一对应。先用当前类别序号 + EncounterId 选中历史记录；
                // 若用坐标优先，StartCombat 后的生成流程会把相邻遭遇的
                // EncounterId 和血量记录相互覆盖。
                var encounterId = encounter.Id.ToString();
                var expected = cursor < category.Count ? category[cursor] : null;
                var matching = expected is not null &&
                               string.Equals(expected.EncounterId, encounterId, StringComparison.Ordinal)
                    ? expected
                    : category.Skip(Math.Min(cursor, category.Count)).FirstOrDefault(record =>
                          string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal))
                      ?? category.FirstOrDefault(record =>
                          string.Equals(record.EncounterId, encounterId, StringComparison.Ordinal));
                _currentRecordedEncounter = matching
                    ?? new EncounterRecord { Act = state.CurrentActIndex, MapKey = mapKey };
                if (!_file.Encounters.Contains(_currentRecordedEncounter))
                    _file.Encounters.Add(_currentRecordedEncounter);
            }
            else
            {
                var mapKey = Key(state.CurrentActIndex, coord);
                _currentRecordedEncounter = _file!.Encounters.FirstOrDefault(x => x.MapKey == mapKey)
                    ?? new EncounterRecord { Act = state.CurrentActIndex, MapKey = mapKey };
                if (!_file.Encounters.Contains(_currentRecordedEncounter))
                    _file.Encounters.Add(_currentRecordedEncounter);
            }

            _currentRecordedEncounter.Act = state.CurrentActIndex;
            _currentRecordedEncounter.EncounterId = encounter.Id.ToString();
            _currentRecordedEncounter.RoomType = encounter.RoomType.ToString();
            _currentRecordedEncounter.Monsters = monsters;
            Volatile.Write(
                ref _currentEncounterStartHp,
                state.Players.FirstOrDefault()?.Creature.CurrentHp ?? -1);
            if (!ReferenceEquals(_currentSkippedEncounter, _currentRecordedEncounter))
                Volatile.Write(ref _currentEncounterSkipped, 0);

            if (ReplayActive)
            {
                var canSkip = (IsCompletedEncounter(_currentRecordedEncounter) ||
                               HasCompletedPreviewEncounter(_currentRecordedEncounter)) &&
                              TryGetPreviewHpLoss(_currentRecordedEncounter, out _);
                Volatile.Write(
                    ref _currentEncounterShouldSkip,
                    canSkip ? 1 : 0);
            }
            else
            {
                Volatile.Write(ref _currentEncounterShouldSkip, 0);
            }
            Save();
        }
        if (ReplayActive)
            EncounterPreviewOverlay.Refresh();
    }

    // 失忆时间线结束后的下一次回归：当前层的遭遇记录清空，重新开始记录。
    public static void ClearCurrentActRecords(int act)
    {
        lock (Sync)
        {
            EnsureLoaded();
            var removed = _file!.Encounters.RemoveAll(x => x.Act == act);
            Save();
            ModLog.Write($"Cleared {removed} encounter records of act {act} after the amnesia timeline.");
        }
    }


    private static bool IsMonsterRoom(RoomType type) => type is RoomType.Monster or RoomType.Elite or RoomType.Boss;

    private static bool IsMonsterRoomType(string roomType) =>
        Enum.TryParse<RoomType>(roomType, out var type) &&
        type is RoomType.Monster or RoomType.Elite;

    private static bool IsStandaloneEventEncounter(EncounterModel encounter) =>
        encounter.GetType().Name.Contains("Event", StringComparison.Ordinal);

    private static bool IsSameCategory(string roomType, bool elite)
    {
        if (!Enum.TryParse<RoomType>(roomType, out var type)) return false;
        return elite ? type == RoomType.Elite : type == RoomType.Monster;
    }
    private static string Key(int act, MapCoord coord) => $"{act}:{coord.col}:{coord.row}";

    private static void EnsureLoaded()
    {
        if (_file is not null) return;
        try
        {
            var sourcePath = File.Exists(JournalPath) ? JournalPath : LegacyPath;
            var json = File.ReadAllText(sourcePath);
            _file = JsonSerializer.Deserialize<EncounterJournalFile>(json) ?? new EncounterJournalFile();
            var normalized = NormalizeEncounterJournal();
            if (normalized || sourcePath == LegacyPath)
                Save();
        }
        catch { _file = new EncounterJournalFile(); }
    }

    private static bool NormalizeEncounterJournal()
    {
        var unique = new List<EncounterRecord>();
        var changed = false;
        foreach (var encounter in _file!.Encounters)
        {
            var duplicate = unique.FirstOrDefault(existing => SameEncounter(existing, encounter));
            if (duplicate is null)
            {
                unique.Add(encounter);
                continue;
            }

            changed = true;
        }

        if (changed)
            _file.Encounters = unique;
        return changed;
    }

    private static bool SameEncounter(EncounterRecord left, EncounterRecord right)
    {
        if (left.Act != right.Act ||
            !string.Equals(left.MapKey, right.MapKey, StringComparison.Ordinal) ||
            !string.Equals(left.EncounterId, right.EncounterId, StringComparison.Ordinal) ||
            !string.Equals(left.RoomType, right.RoomType, StringComparison.Ordinal) ||
            left.Monsters.Count != right.Monsters.Count)
            return false;

        for (var i = 0; i < left.Monsters.Count; i++)
        {
            if (!string.Equals(left.Monsters[i].Id, right.Monsters[i].Id, StringComparison.Ordinal) ||
                !string.Equals(left.Monsters[i].Slot, right.Monsters[i].Slot, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) { ModLog.Write($"Encounter journal save failed: {exception.Message}"); }
    }
}
