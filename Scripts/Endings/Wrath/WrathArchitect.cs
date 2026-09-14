// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 愤怒线进入建筑师事件后，只保留建筑师的一句对白。对白结束时仅由建筑师攻击，
// 因而跳过原版的“建筑师对白 → 玩家回应 → 玩家攻击”三段内容，随后仍复用原版
// TheArchitect.WinRun 完成角色死亡演出、胜利存档与结算。
internal static class WrathArchitectDialogueOverride
{
    private const string LineKey = "wrath-architect-line-0";

    public static void ConfigureDialogue(MegaCrit.Sts2.Core.Models.Events.TheArchitect architect)
    {
        if (!RouteState.IsWrathRoute)
            return;

        var dialogue = new AncientDialogue(new[] { $"return-by-death.{LineKey}" })
        {
            StartAttackers = ArchitectAttackers.None,
            EndAttackers = ArchitectAttackers.Architect
        };

        var line = dialogue.Lines[0];
        line.Speaker = AncientDialogueSpeaker.Ancient;
        line.LineText = new LocString("return-by-death", LineKey);
        line.NextButtonText = new LocString("ancients", "THE_ARCHITECT.CONTINUE");

        var dialogueProperty = AccessTools.Property(
            typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect),
            "Dialogue");
        if (dialogueProperty?.SetMethod is null)
        {
            ModLog.Write("Could not replace the Wrath-route Architect dialogue because the private Dialogue setter was unavailable.");
            return;
        }

        dialogueProperty.SetValue(architect, dialogue);
        ModLog.Write("Wrath-route Architect dialogue replaced: one Architect line, then Architect-only attack.");
    }

    public static bool TryGetText(LocString locString, out string text)
    {
        if (RouteState.IsWrathRoute &&
            string.Equals(locString.LocTable, "return-by-death", StringComparison.Ordinal) &&
            string.Equals(locString.LocEntryKey, LineKey, StringComparison.Ordinal))
        {
            text = "——终于，有寻死的念头了吗？";
            return true;
        }

        text = string.Empty;
        return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Events.TheArchitect), "GenerateInitialOptions")]
internal static class WrathArchitectDialoguePatch
{
    [HarmonyPostfix]
    private static void Postfix(MegaCrit.Sts2.Core.Models.Events.TheArchitect __instance)
        => WrathArchitectDialogueOverride.ConfigureDialogue(__instance);
}
