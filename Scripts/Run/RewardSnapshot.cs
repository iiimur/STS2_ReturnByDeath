// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 原版存档只保存卡牌奖励的卡池与稀有度权重，不保存已掷出的三张卡；
// 载入未完成的战斗房间时会对预完成房间重新 Populate，用存档里的 RNG 状态
// 重掷一遍，导致 SL 前后奖励不一致。这里在每次真实生成卡牌时把结果快照到
// mod 自己的文件；载入后原版重新掷卡时立刻用快照原样还原。
internal static class RewardSnapshotStore
{
    private sealed class SnapshotFile
    {
        public Dictionary<string, List<List<SerializableCard>>> Rooms { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static readonly string SnapshotPath = Path.Combine(
        ModLog.ModDirectory,
        "return-by-death.reward-snapshot.json");
    private static SnapshotFile? _file;
    private static readonly Dictionary<string, int> ConsumedCounts = new();
    private static readonly HashSet<string> SessionCapturedKeys = new();

    // 键唯一对应“本局某一层的某个房间”：层序号、房间序号、玩家。
    // 快照文件在新开局、死亡回归重载与奥托重载时整体清空，避免旧时间线
    // 的快照错误地还原到新房间上。
    private static string Key(CardReward reward) =>
        $"{reward.Player.RunState.CurrentActIndex}|{reward.Player.RunState.CurrentRoomCount}|{reward.Player.NetId}";

    public static void Clear()
    {
        lock (Sync)
        {
            _file = new SnapshotFile();
            ConsumedCounts.Clear();
            SessionCapturedKeys.Clear();
            try { File.Delete(SnapshotPath); } catch { }
        }
    }

    private static SnapshotFile EnsureLoaded()
    {
        if (_file is not null)
            return _file;

        try
        {
            _file = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(SnapshotPath)) ?? new SnapshotFile();
        }
        catch
        {
            _file = new SnapshotFile();
        }

        return _file;
    }

    private static List<SerializableCard> CaptureCards(CardReward reward) =>
        reward.Cards.Select(card => card.ToSerializable()).ToList();

    // 在 CardReward.Populate 后缀调用。regenerated 表示本次真的新生成了卡牌
    // （而不是卡牌已在时的重复调用）；rerolled 表示重掷，需要覆盖最近一份快照。
    public static void Observe(CardReward reward, bool regenerated, bool rerolled)
    {
        lock (Sync)
        {
            var key = Key(reward);
            var file = EnsureLoaded();
            if (!file.Rooms.TryGetValue(key, out var entries))
            {
                entries = new List<List<SerializableCard>>();
                file.Rooms[key] = entries;
            }

            var consumed = ConsumedCounts.TryGetValue(key, out var c) ? c : 0;

            // 本会话尚未生成过该房间的奖励、但磁盘上已有快照，说明这是载入存档
            // 后的重新生成：按生成顺序逐份还原，保证与 SL 前完全一致。
            if (!SessionCapturedKeys.Contains(key) && !rerolled &&
                entries.Count > 0 && consumed < entries.Count)
            {
                RestoreInternal(reward, entries[consumed]);
                ConsumedCounts[key] = consumed + 1;
                return;
            }

            if (!regenerated && !rerolled)
                return;

            SessionCapturedKeys.Add(key);
            if (entries.Count > 0 && rerolled)
            {
                entries[^1] = CaptureCards(reward);
            }
            else
            {
                entries.Add(CaptureCards(reward));
            }

            Save();
        }
    }

    private static void RestoreInternal(CardReward reward, List<SerializableCard> snapshot)
    {
        var cardsField = AccessTools.Field(typeof(CardReward), "_cards");
        var resultCtor = AccessTools.Constructor(typeof(CardCreationResult), new[] { typeof(CardModel) });
        if (cardsField?.GetValue(reward) is not List<CardCreationResult> cards || resultCtor is null)
        {
            ModLog.Write("Card reward snapshot restore skipped: private card list or result constructor unavailable.");
            return;
        }

        cards.Clear();
        foreach (var savedCard in snapshot)
        {
            // RunState.LoadCard 是原版特殊卡奖励同款还原路径：反序列化并登记到
            // 整局卡牌注册表，之后选卡入组仍走原版 CardPileCmd.Add 流程。
            var model = reward.Player.RunState.LoadCard(savedCard, reward.Player);
            cards.Add((CardCreationResult)resultCtor.Invoke(new object[] { model })!);
        }

        ModLog.Write($"Card reward restored from snapshot: {cards.Count} cards.");
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(_file));
        }
        catch (Exception exception)
        {
            ModLog.Write($"Reward snapshot save failed: {exception.Message}");
        }
    }
}

// Populate 在首次生成、载入重掷与重掷（例如漂木遗物）时都会被调用；
// 用 __state 区分“真的重新生成了卡牌”与卡牌已在时的重复调用。
[HarmonyPatch(typeof(CardReward), nameof(CardReward.Populate))]
internal static class RewardSnapshotPopulatePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardReward __instance, out bool __state) =>
        __state = !__instance.IsPopulated;

    [HarmonyPostfix]
    private static void Postfix(CardReward __instance, bool __state)
    {
        try
        {
            var runManager = RunManager.Instance;
            if (runManager is null || !runManager.IsSingleplayerOrFakeMultiplayer)
                return;

            var rerolled = AccessTools.Field(typeof(CardReward), "_hasBeenRerolled")?
                .GetValue(__instance) is true;
            RewardSnapshotStore.Observe(__instance, __state, rerolled);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Card reward snapshot observe failed: {exception}");
        }
    }
}
