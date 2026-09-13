// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class CosmeticAudio
{
    private static readonly string[] DeathScreamFiles =
    {
        "惨叫/惨叫1.wav",
        "惨叫/惨叫2.wav",
        "惨叫/惨叫3.wav",
        "惨叫/惨叫4.wav",
        "惨叫/惨叫5.wav",
        "惨叫/惨叫6.wav",
        "惨叫/惨叫7.wav",
        "惨叫/惨叫8.wav",
        "惨叫/惨叫9.wav",
        "惨叫/惨叫10.wav"
    };

    // 涅奥/第一层先古台词统一从“我爱你”音效组随机抽取。路径相对于
    // Resource/音效/，构建时会由 csproj 保留该子目录。
    private static readonly string[] LoveYouFiles =
    {
        "我爱你/我爱你音效.wav",
        "我爱你/我爱你1.wav",
        "我爱你/我爱你2.wav",
        "我爱你/我爱你3.wav",
        "我爱你/我爱你4.wav",
        "我爱你/我爱你5.wav",
        "我爱你/我爱你6.wav",
        "我爱你/我爱你7.wav",
        "我爱你/我爱你8.wav",
        "我爱你/我爱你9.wav",
        "我爱你/我爱你10.wav",
        "我爱你/我爱你11.wav",
        "我爱你/我爱你12.wav",
        "我爱你/我爱你13.wav",
        "我爱你/我爱你14.wav"
    };

    private static readonly WavPlayer Player = new("cosmetic");

    public static bool TryPlayRandomDeathScream()
    {
        // Random.Shared 可安全地用于游戏的异步/多线程回调；每次真正死亡时
        // 从 10 个素材中独立抽取一个，不改变回归音效的首次/后续播放规则。
        var fileName = DeathScreamFiles[Random.Shared.Next(DeathScreamFiles.Length)];
        ModLog.Write($"Selected random death scream: {fileName}.");
        return Player.TryPlay(fileName);
    }

    public static bool TryPlayRandomLoveYou()
    {
        var fileName = LoveYouFiles[Random.Shared.Next(LoveYouFiles.Length)];
        ModLog.Write($"Selected random love-you voice line: {fileName}.");
        return Player.TryPlay(fileName);
    }

    public static bool TryPlay(string fileName, float volume = 1f, Action? onFinished = null) =>
        Player.TryPlay(fileName, volume, onFinished);

    public static void Stop() => Player.Stop();
}

[HarmonyPatch(typeof(SfxCmd), nameof(SfxCmd.Play), new[] { typeof(string), typeof(float) })]
internal static class NeowWelcomeSfxPatch
{
    [HarmonyPrefix]
    private static bool Prefix(object[] __args)
        => TryReplaceNeowDialogue(__args);

    internal static bool TryReplaceNeowDialogue(object[] args)
    {
        if (args.Length == 0 || args[0] is not string path ||
            !IsNeowDialogueSfx(path))
        {
            return true;
        }

        ModLog.Write($"Intercepted Neow SFX event: {path}");
        if (CosmeticAudio.TryPlayRandomLoveYou())
        {
            ModLog.Write($"Replaced Neow SFX event: {path}");
            return false;
        }

        return true;
    }

    private static bool IsNeowDialogueSfx(string path) =>
        path.StartsWith("event:/sfx/npcs/neow/", StringComparison.Ordinal);
}

// “杀死……建筑师”等特殊初见台词使用的音效路径不一定位于 Neow
// 文件夹下。它们都会经过 NAncientDialogueLine.PlaySfx，因此在第一层
// 先古对话行这里直接替换，避免依赖具体音频资源路径。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Events.NAncientDialogueLine), "PlaySfx")]
internal static class FirstAncientDialogueSfxPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null || state.CurrentActIndex != 0)
            return true;

        ModLog.Write("Intercepted first Ancient dialogue-line SFX.");
        if (CosmeticAudio.TryPlayRandomLoveYou())
        {
            ModLog.Write("Replaced first Ancient dialogue-line SFX with a random love-you voice line.");
            return false;
        }

        return true;
    }
}

// 某些先古台词使用带额外参数的 SfxCmd.Play 重载；例如“杀死……建筑师”
// 不一定会走 Play(string, float)，因此必须覆盖这个重载。
[HarmonyPatch(typeof(SfxCmd), nameof(SfxCmd.Play), new[] { typeof(string), typeof(string), typeof(float), typeof(float) })]
internal static class NeowDialogueSfxExtendedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(object[] __args) =>
        NeowWelcomeSfxPatch.TryReplaceNeowDialogue(__args);
}

// 兼容直接由 NAudioManager 播放的一次性 Neow 音效入口。
[HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.PlayOneShot), new[] { typeof(string), typeof(float) })]
internal static class NeowDialogueOneShotPatch
{
    [HarmonyPrefix]
    private static bool Prefix(object[] __args) =>
        NeowWelcomeSfxPatch.TryReplaceNeowDialogue(__args);
}

[HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.PlayOneShot), new[] { typeof(string), typeof(Dictionary<string, float>), typeof(float) })]
internal static class NeowDialogueParameterizedOneShotPatch
{
    [HarmonyPrefix]
    private static bool Prefix(object[] __args) =>
        NeowWelcomeSfxPatch.TryReplaceNeowDialogue(__args);
}

[HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.PlayMusic), new[] { typeof(string) })]
internal static class GameOverMusicPatch
{
    private const string TargetPath = "event:/temp/sfx/game_over";

    [HarmonyPrefix]
    private static bool Prefix(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is not string path ||
            !string.Equals(path, TargetPath, StringComparison.Ordinal))
        {
            return true;
        }

        // 傲慢线建筑师第三个“继续”会进入同一个原版 game-over 音频入口；
        // 只替换这一次终局死亡结算音效，建筑师流程本身仍完全走原版。
        if (ArchitectFinaleState.IsActive && RouteState.IsPrideRoute)
        {
            ModLog.Write(PrideArchitectDeathAudio.WasStarted
                ? "Suppressed native game-over audio after third-Continue pride death audio started."
                : "Suppressed native game-over audio during pride Architect finale.");
            return false;
        }

        if (SlothFinalBossTransition.IsDeathSettlementActive)
        {
            ModLog.Write("Suppressed native game-over audio after Sloth finale death WAV started.");
            return false;
        }

        // 一般路线的建筑师终局保留原版 game-over BGM，不使用死亡惨叫替换。
        if (ArchitectFinaleState.IsActive && !RouteState.IsIfRoute)
        {
            ModLog.Write("Restored native game-over music for the normal Architect finale.");
            return true;
        }

        // 「强欲」IF 线：死亡保持安静——既不播放惨叫，也不播放原生 game-over 音效。
        if (GreedRoute.IsActive)
        {
            ModLog.Write("Suppressed death audio in the Greed IF route.");
            return false;
        }

        ModLog.Write($"Intercepted game-over music: {TargetPath}");
        if (CosmeticAudio.TryPlayRandomDeathScream())
        {
            ModLog.Write("Replaced game-over music with a random death scream.");
            return false;
        }

        return true;
    }
}

// NRunMusicController 是每局流程背景音乐的入口。专属音乐期间只跳过它的
// BGM 更新，并仍调用 UpdateAmbience，因此环境音和所有 PlayOneShot 音效不受影响。
[HarmonyPatch(typeof(NGame), "LoadRun")]
internal static class EncounterPreviewLoadPatch
{
    [HarmonyPostfix]
    private static void Postfix(Task __result)
    {
        if (EncounterJournalStore.ReplayActive)
            _ = RefreshAfterLoadAsync(__result);
        // 载入完成后把左上角头像变成记忆按钮（失败不影响载入）。
        _ = EnsureMemoryButtonAfterLoadAsync(__result);
        if (OttoPendingCleanup.IsPending)
            _ = OttoPendingCleanup.ApplyAfterLoadAsync(__result);
        if (OttoAcceptanceMusic.IsSuppressingNativeRunMusic)
            _ = OttoAcceptanceMusic.StartAfterLoadAsync(__result);
    }

    private static async Task EnsureMemoryButtonAfterLoadAsync(Task loadTask)
    {
        try
        {
            await loadTask;
            AmnesiaMemoryPanel.EnsureButton();
        }
        catch (Exception exception)
        {
            // 记录完整堆栈：载入任务 fault 意味着黑屏，需要堆栈定位来源。
            ModLog.Write($"Memory button load hook failed: {exception}");
        }
    }

    private static async Task RefreshAfterLoadAsync(Task loadTask)
    {
        await loadTask;
        EncounterPreviewOverlay.Refresh();
    }
}
