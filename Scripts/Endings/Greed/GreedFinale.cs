// 「强欲」IF 线的终局：进入强欲线后，在第三层（act 2）满足以下任一条件
// 即进行游戏胜利结算——结算画面与普通结局被建筑师杀死后完全一致
// （NGameOverScreen 以 CurrentRoom.IsVictoryRoom 判定胜负变体）：
//   1. 第三层的第二次死亡：死亡结算直接转为胜利结算，不再回归；
//   2. 第一个宝箱房点击“前进”：跳过返回地图，直接进入胜利结算。
// 胜利结算复用原生 RunManager.WinRun（建筑师胜利分支的同一入口）。

using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Rooms;

namespace ReturnByDeath;

// 强欲线的稳健判定：内存标记或持有强欲之心（覆盖中途重启游戏导致内存
// 标记丢失的情况——遗物本身写在检查点里）。
internal static class GreedRoute
{
    public static bool IsActive =>
        GreedIfState.HasEntered ||
        RunManager.Instance?.DebugOnlyGetState()?.Players.Any(player =>
            player.Relics.Any(relic => relic is HeartOfGreed)) == true;
}

internal static class GreedFinaleState
{
    private sealed class FinaleFile
    {
        public string? RunKey { get; set; }
        public int DeathsInAct3 { get; set; }
    }

    private static readonly string StatePath = Path.Combine(
        ModLog.ModDirectory, "return-by-death.greed-finale.json");
    private static readonly object Sync = new();
    private static int _presentingVictory;

    // 让 NGameOverScreen 把当前房间判定为胜利房（与建筑师事件房同款待遇）。
    public static bool PresentingVictory => Volatile.Read(ref _presentingVictory) != 0;

    public static void MarkPresentingVictory() => Volatile.Write(ref _presentingVictory, 1);

    public static void Reset()
    {
        Volatile.Write(ref _presentingVictory, 0);
        try { File.Delete(StatePath); }
        catch (Exception exception) { ModLog.Write($"Could not clear greed finale state: {exception.Message}"); }
    }

    // 记录第三层死亡并判断是否第二次（触发终局）。死亡计数按局持久化，
    // 中途重启游戏也不会把第二次死亡误判成第一次。
    public static bool ShouldConvertDeathToVictory(string runKey)
    {
        lock (Sync)
        {
            var file = Load(runKey);
            file.DeathsInAct3++;
            Save(file);
            if (file.DeathsInAct3 < 2)
            {
                ModLog.Write($"Greed finale: act-3 death #{file.DeathsInAct3}; normal recovery continues.");
                return false;
            }

            Volatile.Write(ref _presentingVictory, 1);
            return true;
        }
    }

    private static FinaleFile Load(string runKey)
    {
        try
        {
            var file = JsonSerializer.Deserialize<FinaleFile>(File.ReadAllText(StatePath));
            if (file is not null && file.RunKey == runKey)
                return file;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Greed finale state load failed; starting fresh: {exception.Message}");
        }
        return new FinaleFile { RunKey = runKey };
    }

    private static void Save(FinaleFile file)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(file)); }
        catch (Exception exception) { ModLog.Write($"Greed finale state save failed: {exception.Message}"); }
    }
}

// 结算画面的胜负判定：强欲终局期间把当前房间报告为胜利房。
[HarmonyPatch(typeof(AbstractRoom), "IsVictoryRoom", MethodType.Getter)]
internal static class GreedVictoryRoomPatch
{
    [HarmonyPostfix]
    private static void Postfix(AbstractRoom __instance, ref bool __result)
    {
        if (__result || !GreedFinaleState.PresentingVictory)
            return;

        var currentRoom = RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom;
        if (currentRoom is not null && ReferenceEquals(currentRoom, __instance))
            __result = true;
    }
}

// 触发点一：第三层的第二次死亡。在死亡结算入口把 isVictory 翻转为 true，
// 原版按胜利完成存档与历史记录（与建筑师击杀的结算路径一致），
// 回归流程因 isVictory=true 自然跳过。
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded), new[] { typeof(bool) })]
internal static class GreedFinaleDeathPatch
{
    [HarmonyPrefix]
    private static void Prefix(RunManager __instance, ref bool isVictory)
    {
        if (isVictory || !GreedRoute.IsActive)
            return;

        var state = __instance.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 2)
            return;

        if (!CheckpointStore.TryLoad(out var checkpoint))
            return;

        if (GreedFinaleState.ShouldConvertDeathToVictory(CheckpointStore.GetRunKey(checkpoint)))
        {
            isVictory = true;
            ModLog.Write("Greed finale: second act-3 death converted into the victory settlement.");
        }
    }
}

// 触发点二：第三个宝箱房点击“前进”。跳过返回地图，直接走 WinRun
// （原生建筑师胜利分支的同一入口：按胜利完成存档，随后击杀全部玩家
// 触发胜利结算画面）。
[HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonPressed", new[] { typeof(NButton) })]
internal static class GreedFinaleTreasurePatch
{
    private static int _started;

    [HarmonyPrefix]
    private static bool Prefix(NTreasureRoom __instance)
    {
        if (Volatile.Read(ref _started) != 0 || !GreedRoute.IsActive)
            return true;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var room = AccessTools.Field(typeof(NTreasureRoom), "_room")?.GetValue(__instance) as TreasureRoom;
        if (state is null ||
            state.CurrentActIndex != 2 ||
            room is null ||
            !ReferenceEquals(state.CurrentRoom, room))
            return true;

        Volatile.Write(ref _started, 1);
        GreedFinaleState.MarkPresentingVictory();
        TaskHelper.RunSafely(EnterVictorySettlementAsync());
        ModLog.Write("Greed finale: treasure-room proceed converted into the victory settlement.");
        return false;
    }

    private static async Task EnterVictorySettlementAsync()
    {
        try
        {
            var runManager = RunManager.Instance
                ?? throw new InvalidOperationException("RunManager is unavailable for the Greed finale.");
            var winRun = AccessTools.Method(typeof(RunManager), "WinRun", Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(RunManager).FullName, "WinRun");
            if (winRun.Invoke(runManager, null) is not Task winTask)
                throw new InvalidOperationException("Native WinRun did not return a Task.");
            await winTask;
            ModLog.Write("Greed finale entered native victory settlement.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Greed finale victory settlement failed: {exception}");
        }
    }
}
