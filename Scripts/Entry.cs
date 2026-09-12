using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace ReturnByDeath;

/// <summary>
/// Mod 的加载入口。
/// 游戏读取 ReturnByDeath.json 后，会按照 ModInitializer 注册的方法名调用 Init。
/// </summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    /// <summary>
    /// 安装本 mod 的 Harmony 补丁，并恢复可能因重启而中断的遭遇预告重放状态。
    /// </summary>
    public static void Init()
    {
        // Harmony 是运行时补丁框架；PatchAll 会扫描当前程序集中的 HarmonyPatch 类。
        // ID 只要保持唯一即可，不需要与游戏内显示名称相同。
        new Harmony("ReturnByDeath").PatchAll(Assembly.GetExecutingAssembly());

        // 回归过程可能跨越一次游戏进程重启，因此恢复持久化的重放标记。
        EncounterJournalStore.RestoreReplayMode();
        ModLog.Write("Initialized.");
    }
}
