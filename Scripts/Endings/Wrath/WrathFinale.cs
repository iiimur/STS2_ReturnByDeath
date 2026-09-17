// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 「愤怒」IF 线的终局音效：正常打完第三层 Boss 进入建筑师、显示愤怒线唯一一句
// 建筑师对白后，点击“继续”（原版 Proceed 选项 → TheArchitect.WinRun）时，稍作
// 停顿再插入 愤怒结局ED.wav（16-bit PCM，随构建复制到 mod 的 音效/ 目录）。
//
// 同时愤怒线的这次终局死亡不播放惨叫（见 AudioOverrides.GameOverMusicPatch）。
internal static class WrathFinaleState
{
    private const string FinaleAudioFileName = "愤怒结局ED.wav";

    // 点击唯一一次“继续”后先让原版建筑师攻击演出跑起来，再插入结局音效。
    private const double PreAudioPauseSeconds = 0.5d;

    private static int _started;

    public static void Reset() => Volatile.Write(ref _started, 0);

    // 在最后一个“继续”（原版 Proceed 选项 → TheArchitect.WinRun）被触发时调用。
    // 只在愤怒线的建筑师终局生效；一次性标记防止重复插播。
    public static void TryScheduleFinaleAudio()
    {
        if (!RouteState.IsWrathRoute || !ArchitectFinaleState.IsActive ||
            Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        IfAchievements.Unlock("wrath_ending");
        ModLog.Write("Wrath finale reached: the final Architect Continue was chosen; wrath_ending unlocked.");

        // 只把音频启动延后；原版 WinRun 的建筑师攻击与胜利结算立即继续，
        // 不会被这次暂停拖慢。
        _ = StartAudioAfterDelayAsync();
    }

    private static async Task StartAudioAfterDelayAsync()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is not null)
            {
                // 使用 Godot 计时器而非 Task.Delay，确保暂停和后续播放都留在游戏主线程。
                var timer = tree.CreateTimer(PreAudioPauseSeconds, true, false, true);
                await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitSignal(
                    timer,
                    SceneTreeTimer.SignalName.Timeout,
                    tree.Root);
            }

            if (!CosmeticAudio.TryPlay(FinaleAudioFileName, 0.5f))
                ModLog.Write($"Wrath finale audio could not start: {FinaleAudioFileName}");
            else
                ModLog.Write("Wrath finale: wrath ending audio started after the final Architect continue.");
        }
        catch (Exception exception)
        {
            // 音频失败绝不能影响原版终局结算。
            ModLog.Write($"Wrath finale audio delay failed: {exception.Message}");
        }
    }
}

// 愤怒线唯一一句对白后的“继续”是 Proceed 选项，其动作即 TheArchitect.WinRun。
// WrathArchitectDialogueOverride 已将 EndAttackers 设为 Architect，所以这里进入的
// 原版流程只播放建筑师攻击，随后结束游戏；前缀仅负责挂上愤怒结局音效。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "WinRun")]
internal static class WrathFinaleContinuePatch
{
    [HarmonyPrefix]
    private static void Prefix() =>
        WrathFinaleState.TryScheduleFinaleAudio();
}
