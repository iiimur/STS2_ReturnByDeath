// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 傲慢线建筑师对白覆盖。原版对白仍由 TheArchitect 事件正常推进，
// 这里只替换当前三条 DialogueLine 的显示文本，不影响事件状态、动画和终局结算。
internal static class PrideArchitectDialogueOverride
{
    private static AncientDialogueLine? _currentLine;
    private static int _currentLineIndex = -1;

    public static void SetCurrentLine(MegaCrit.Sts2.Core.Models.Events.TheArchitect architect)
    {
        if (!RouteState.IsPrideRoute)
        {
            _currentLine = null;
            _currentLineIndex = -1;
            return;
        }

        var dialogue = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "Dialogue")?
            .GetValue(architect) as AncientDialogue;
        var index = (int)(AccessTools.Property(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "CurrentLineIndex")?
            .GetValue(architect) ?? -1);
        _currentLine = dialogue is not null && index >= 0 && index < dialogue.Lines.Count
            ? dialogue.Lines[index]
            : null;
        _currentLineIndex = index;
    }

    // 对话的第 3 句已经完整显示、但尚未点击它的 Continue 时为 true。
    // NEventRoom.BeforeOptionChosen 正好发生在该次点击实际推进原版事件之前。
    public static bool IsFinalLineVisible => _currentLineIndex == 2;

    public static void ConfigureDialogue(MegaCrit.Sts2.Core.Models.Events.TheArchitect architect)
    {
        if (!RouteState.IsPrideRoute)
            return;

        var dialogue = new AncientDialogue(new[]
        {
            "return-by-death.pride-architect-line-0",
            "return-by-death.pride-architect-line-1",
            "return-by-death.pride-architect-line-2"
        })
        {
            StartAttackers = ArchitectAttackers.None,
            // 保留原版“玩家攻击 → 建筑师攻击”的终局演出顺序。
            // 玩家攻击的数字会由下方补丁临时归零，但攻击动画本身照常播放。
            EndAttackers = ArchitectAttackers.Both
        };

        for (var i = 0; i < dialogue.Lines.Count; i++)
        {
            var line = dialogue.Lines[i];
            line.Speaker = i == 1
                ? AncientDialogueSpeaker.Character
                : AncientDialogueSpeaker.Ancient;
            line.LineText = new LocString("return-by-death", $"pride-architect-line-{i}");
            line.NextButtonText = new LocString("ancients", "THE_ARCHITECT.CONTINUE");
        }

        var dialogueProperty = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "Dialogue");
        if (dialogueProperty?.SetMethod is null)
        {
            ModLog.Write("Could not replace Architect dialogue because the private Dialogue setter was unavailable.");
            return;
        }

        dialogueProperty.SetValue(architect, dialogue);
        ModLog.Write("Pride-route Architect dialogue replaced with the fixed three-line sequence.");
    }

    public static bool TryGetText(LocString locString, out string text)
    {
        if (!string.Equals(locString.LocTable, "return-by-death", StringComparison.Ordinal))
        {
            text = string.Empty;
            return false;
        }

        // 建筑师对白只在傲慢线上生效；其余 return-by-death 键不受路线限制。
        var isPrideLine = locString.LocEntryKey.StartsWith("pride-architect-line-", StringComparison.Ordinal);
        if (isPrideLine && !RouteState.IsPrideRoute)
        {
            text = string.Empty;
            return false;
        }

        text = locString.LocTable switch
        {
            "return-by-death" => locString.LocEntryKey switch
            {
                "pride-architect-line-0" => "到此为止了哦，恶党",
                "pride-architect-line-1" => "这是能够让你成为国王的，唯一的方法。",
                "pride-architect-line-2" => "为什么……",
                "rem-reward-prompt" => "选择2张升级过的稀有卡，加入你的卡组",
                "preview-normal-title" => "普通怪物",
                "preview-elite-title" => "精英怪物",
                "preview-unknown-title" => "问号",
                "preview-shop-title" => "商店",
                _ => string.Empty
            },
            _ => string.Empty
        };
        return text.Length != 0;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "PlayCurrentLine")]
internal static class PrideArchitectPlayCurrentLinePatch
{
    [HarmonyPrefix]
    private static void Prefix(MegaCrit.Sts2.Core.Models.Events.TheArchitect __instance) =>
        PrideArchitectDialogueOverride.SetCurrentLine(__instance);
}

// 对话生成完毕时，保留原版建筑师终局结算：三句对白结束后，先由玩家攻击建筑师，
// 再由建筑师攻击玩家。玩家攻击的伤害数字由下方补丁固定为 0。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "GenerateInitialOptions")]
internal static class PrideArchitectAttackOrderPatch
{
    [HarmonyPostfix]
    private static void Postfix(MegaCrit.Sts2.Core.Models.Events.TheArchitect __instance)
        => PrideArchitectDialogueOverride.ConfigureDialogue(__instance);
}

// 建筑师事件的玩家攻击并不走普通 Combat 伤害，而是先由 TheArchitect.DivideWildly
// 将 Score 拆成多段，再逐段生成 NDamageNumVfx。原版分段函数即使收到 0 也会给每段
// 至少 1 点，因此直接把这个函数的返回数组替换为全 0，保留挥击、命中、屏幕震动和
// 伤害数字演出，但不会对建筑师造成实际伤害。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "DivideWildly")]
internal static class PrideArchitectZeroDamagePartsPatch
{
    [HarmonyPrefix]
    private static bool Prefix(int __1, ref int[] __result)
    {
        if (!RouteState.IsPrideRoute)
            return true;

        __result = new int[Math.Max(0, __1)];
        ModLog.Write($"Pride-route Architect player attack damage parts replaced with {__result.Length} zero values.");
        return false;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
internal static class PrideArchitectFormattedTextPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LocString __instance, ref string __result)
    {
        if (PrideArchitectDialogueOverride.TryGetText(__instance, out var text))
        {
            __result = text;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
internal static class PrideArchitectRawTextPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LocString __instance, ref string __result)
    {
        if (PrideArchitectDialogueOverride.TryGetText(__instance, out var text))
        {
            __result = text;
            return false;
        }

        return true;
    }
}

// 建筑师在程序层面是 Encounter，但在玩法层面是整局真正的终点。
// 这里把它提升为全局状态，供死亡结算、音效和遭遇预告共同判断。
internal static class PrideArchitectDeathAudio
{
    private const double PreAudioPauseSeconds = 2.2d;
    private static int _started;

    public static bool WasStarted => Volatile.Read(ref _started) != 0;

    public static void Reset() => Volatile.Write(ref _started, 0);

    public static Task PauseThenStartAndContinueAsync(Task nativeBeforeChoice)
    {
        if (!RouteState.IsPrideRoute || !ArchitectFinaleState.IsActive ||
            !PrideArchitectDialogueOverride.IsFinalLineVisible ||
            Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return nativeBeforeChoice;
        }

        // 只把音频启动延后；原版 Continue 的后续动作必须立即继续，
        // 否则建筑师的 0 伤害攻击、死亡和结算都会被一起延迟。
        _ = StartAudioAfterDelayAsync();
        return nativeBeforeChoice;
    }

    private static async Task StartAudioAfterDelayAsync()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is not null)
            {
                // 使用 Godot 计时器而非 Task.Delay，确保暂停和后续事件推进都留在游戏主线程。
                var timer = tree.CreateTimer(PreAudioPauseSeconds, true, false, true);
                await MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil.AwaitSignal(
                    timer,
                    SceneTreeTimer.SignalName.Timeout,
                    tree.Root);
            }

            ModLog.Write("Started pride Architect death-settlement audio after the third Continue 3000ms pause.");
            if (!CosmeticAudio.TryPlay("傲慢结局死亡结算.wav"))
                ModLog.Write("Pride Architect death-settlement audio could not start after the third Continue pause.");
        }
        catch (Exception exception)
        {
            // 即便暂停演出出错，也必须放行原版建筑师终局，不能让对话卡死。
            ModLog.Write($"Pride Architect third-Continue audio delay failed: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(NEventRoom), "BeforeOptionChosen", new[] { typeof(EventOption) })]
internal static class PrideArchitectFinalContinueAudioPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result) =>
        __result = PrideArchitectDeathAudio.PauseThenStartAndContinueAsync(__result);
}
