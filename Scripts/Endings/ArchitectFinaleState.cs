// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class ArchitectFinaleState
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) != 0;

    public static void Reset()
    {
        Volatile.Write(ref _active, 0);
        PrideArchitectDeathAudio.Reset();
    }

    public static bool Observe(EncounterModel encounter)
    {
        if (!IsArchitectEncounter(encounter))
            return false;

        if (Interlocked.Exchange(ref _active, 1) == 0)
        {
            RecoveryMarker.StopRecoveryForArchitect();
            EncounterJournalStore.StopForArchitect();
            ModLog.Write("Architect finale detected: native ending, game-over audio, and encounter preview restored.");
        }

        return true;
    }

    public static bool IsArchitectEncounter(EncounterModel encounter)
    {
        if (encounter is MegaCrit.Sts2.Core.Models.Encounters.TheArchitectEventEncounter)
            return true;

        // StartCombat 在生成怪物前就会进入某些补丁；此时访问 MonstersWithSlots
        // 会抛异常。仅在本地 API 已完成生成后再用怪物类型作兼容性判定。
        return encounter.HaveMonstersBeenGenerated &&
               encounter.MonstersWithSlots.Any(x => x.Item1 is MegaCrit.Sts2.Core.Models.Monsters.Architect);
    }
}

// 傲慢线建筑师的终局音效必须由第三次 Continue 点击触发，而不是等待原版死亡结算。
// 之后仍会进入原版 game_over 音频入口；届时只截停原版音效，避免重叠或重复播放。
