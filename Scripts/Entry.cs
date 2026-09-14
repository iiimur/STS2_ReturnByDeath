using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models.RelicPools;

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

        // 三个强欲遗物挂到 FallbackRelicPool：这只让 RelicModel.Pool 能解析
        // （悬停提示、能量图标颜色需要），该池不参与商店与奖励生成。
        ModHelper.AddModelToPool<FallbackRelicPool, PainOfThePast>();
        ModHelper.AddModelToPool<FallbackRelicPool, SacrificeOfThePresent>();
        ModHelper.AddModelToPool<FallbackRelicPool, BonesOfTheFuture>();
        ModHelper.AddModelToPool<FallbackRelicPool, HeartOfGreed>();
        ModHelper.AddModelToPool<FallbackRelicPool, OttoContract>();
        ModHelper.AddModelToPool<FallbackRelicPool, HeartOfSloth>();
        ModHelper.AddModelToPool<FallbackRelicPool, HeartOfPride>();
        ModHelper.AddModelToPool<FallbackRelicPool, HeartOfWrath>();

        // 新遗物的中文词条：若 mod 初始化晚于首次语言表加载，这里补一次注入。
        GreedRelicText.Inject();
        ModLog.Write("Initialized.");
    }
}
