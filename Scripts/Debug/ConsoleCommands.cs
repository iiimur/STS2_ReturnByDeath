// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 仅供本地测试使用的控制台命令：
//   boss pride
//   boss sloth
//   boss greed
//   ending 1～3  （按列表序号切换结局成就）
//   event 1～7   （按列表序号切换事件成就）
//
// boss 系列只开启对应 IF 线；第三层和 BOSS 房间由测试者自己使用原版
// 控制台命令进入。ending/event 系列按上面数组的展示顺序切换成就状态。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "ProcessCommand")]
internal static class ReturnByDeathConsoleCommandPatch
{
    private const string TestCommand = "boss pride";
    private const string SlothTestCommand = "boss sloth";
    private const string GreedTestCommand = "boss greed";
    private const string OpenEyeCommand = "map reveal";

    [HarmonyPrefix]
    private static bool Prefix(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole __instance,
        object[] __args)
    {
        return !TryHandleCommand(__instance, __args, allowInputBufferFallback: __args.Length == 0);
    }

    internal static bool TryHandleCommand(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console,
        object[] args,
        bool allowInputBufferFallback)
    {
        // open_eye：切换“全知之眼”，本层地图全部节点与路径线直接可见。
        if (MatchesCommand(args, OpenEyeCommand) ||
            (allowInputBufferFallback && MatchesCommand(ReadInputBuffer(console), OpenEyeCommand)))
        {
            MapNodeVisibilityFilter.ToggleOpenEye();
            ClearConsoleInput(console, "open_eye", "full map revealed");
            return true;
        }

        if (TryGetIndexedCommand(
                args, console, allowInputBufferFallback, "ending", out var endingIndex))
        {
            ToggleAchievementByIndex(
                console, "ending", endingIndex, IfAchievements.EndingAchievements);
            return true;
        }

        if (TryGetIndexedCommand(
                args, console, allowInputBufferFallback, "event", out var eventIndex))
        {
            ToggleAchievementByIndex(
                console, "event", eventIndex, IfAchievements.EventAchievements);
            return true;
        }

        if (MatchesTestCommand(args, TestCommand, console, allowInputBufferFallback))
        {
            RouteState.EnterPrideRoute();
            ArchitectFinaleState.Reset();
            PrideFinalBossOpening.ResetForNewRun();
            PrideFinalBossTransition.ResetForNewRun();
            ClearConsoleInput(console, TestCommand, "pride route enabled");
            return true;
        }

        if (MatchesTestCommand(args, SlothTestCommand, console, allowInputBufferFallback))
        {
            ArchitectFinaleState.Reset();
            PrideFinalBossOpening.ResetForNewRun();
            PrideFinalBossTransition.ResetForNewRun();
            RouteState.EnterSlothRoute();
            ClearConsoleInput(console, SlothTestCommand, "sloth route enabled; map reveal and free travel enabled");
            return true;
        }

        if (MatchesTestCommand(args, GreedTestCommand, console, allowInputBufferFallback))
        {
            GreedIfState.Mark();
            ClearConsoleInput(console, GreedTestCommand, "greed IF route enabled");
            return true;
        }

        return false;
    }

    // 自定义命令走 Prefix 拦截后，原版 ProcessCommand 的尾部（回显+清空输入）
    // 会被整段跳过，导致命令残留在输入框里。这里复刻原版尾部行为：
    // 绿色回显命令行、输出结果、清空输入与 Tab 缓冲。
    private static void ClearConsoleInput(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console,
        string command,
        string result)
    {
        try
        {
            var outputBuffer = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "_outputBuffer")?
                .GetValue(console) as RichTextLabel;
            var symbolPrompt = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "_symbolPrompt")?
                .GetValue(console) as string ?? ">";
            var inputBuffer = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "_inputBuffer")?
                .GetValue(console) as LineEdit;
            var tabBuffer = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "_tabBuffer")?
                .GetValue(console) as RichTextLabel;

            if (outputBuffer is not null)
                outputBuffer.Text += $"[color=#00ff00]{symbolPrompt}[/color] {command}\n" +
                                     $"[color=#00ff00]{result}\n[/color]";
            if (inputBuffer is not null)
                inputBuffer.Text = string.Empty;
            if (tabBuffer is not null)
                tabBuffer.Text = string.Empty;
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "DisableTabBuffer")?
                .Invoke(console, null);
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "HideGhostText")?
                .Invoke(console, null);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Console input clear failed: {exception.Message}");
        }
    }

    internal static bool IsCustomCommandInInputBuffer(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console)
    {
        var input = ReadInputBuffer(console);
        return MatchesCommand(input, TestCommand) ||
               MatchesCommand(input, SlothTestCommand) ||
               MatchesCommand(input, GreedTestCommand) ||
               TryParseIndexedCommand(input, "ending", out _) ||
               TryParseIndexedCommand(input, "event", out _);
    }

    private static void ToggleAchievementByIndex(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console,
        string commandName,
        int oneBasedIndex,
        IReadOnlyList<IfAchievements.Achievement> achievements)
    {
        var command = $"{commandName} {oneBasedIndex}";
        if (oneBasedIndex < 1 || oneBasedIndex > achievements.Count)
        {
            ClearConsoleInput(console, command,
                $"invalid index; valid range is 1-{achievements.Count}");
            return;
        }

        var achievement = achievements[oneBasedIndex - 1];
        var unlocked = IfAchievements.Toggle(achievement.Id);
        ClearConsoleInput(console, command,
            $"{achievement.Name}: {(unlocked ? "unlocked" : "hidden")}");
    }

    private static bool TryGetIndexedCommand(
        object[] args,
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console,
        bool allowInputBufferFallback,
        string commandName,
        out int index)
    {
        if (TryParseIndexedCommand(args, commandName, out index))
            return true;

        return allowInputBufferFallback &&
               TryParseIndexedCommand(ReadInputBuffer(console), commandName, out index);
    }

    private static bool TryParseIndexedCommand(
        object? value,
        string commandName,
        out int index)
    {
        index = 0;
        if (value is string text)
        {
            var tokens = text.Trim().Split(
                [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length == 2 &&
                   string.Equals(tokens[0], commandName, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(tokens[1], out index);
        }

        if (value is not System.Collections.IEnumerable enumerable)
            return false;

        var pieces = new List<string>();
        foreach (var item in enumerable)
        {
            if (TryParseIndexedCommand(item, commandName, out index))
                return true;
            if (item is string piece && !string.IsNullOrWhiteSpace(piece))
                pieces.Add(piece.Trim());
        }

        return pieces.Count > 0 &&
               TryParseIndexedCommand(string.Join(' ', pieces), commandName, out index);
    }

    private static bool MatchesTestCommand(
        object[] args,
        string command,
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console,
        bool allowInputBufferFallback) =>
        MatchesCommand(args, command) ||
        (allowInputBufferFallback && MatchesCommand(ReadInputBuffer(console), command));

    private static bool MatchesCommand(object? value, string expected)
    {
        if (value is string text)
        {
            var command = text.Trim();
            if (string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                return true;

            // 某些版本会把整行或带参数的输入作为一个字符串传入。
            var firstToken = command.Split([' ', '\t', '\r', '\n'], 2)[0];
            return string.Equals(firstToken, expected, StringComparison.OrdinalIgnoreCase);
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (MatchesCommand(item, expected))
                    return true;
            }
        }

        return false;
    }

    private static string? ReadInputBuffer(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole console)
    {
        // 另一些版本的 ProcessCommand 不直接传入文本，而是从 _inputBuffer 读取。
        // 这里只读取，不修改原版控制台状态。
        var field = AccessTools.Field(
            typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole),
            "_inputBuffer");
        var property = AccessTools.Property(
            typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole),
            "_inputBuffer");
        var buffer = property?.GetValue(console) ?? field?.GetValue(console);
        if (buffer is string text)
            return text;

        if (buffer is null)
            return null;

        var textProperty = AccessTools.Property(buffer.GetType(), "Text");
        return textProperty?.GetValue(buffer) as string;
    }

}

// ProcessCommand 在部分版本中只会在命令被原版解析器接受后调用；未知命令因此无法
// 进入上面的补丁。_Input 是 Godot 控制台收到回车事件的更早入口，直接在这里接管测试命令。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole), "_Input")]
internal static class ReturnByDeathConsoleSubmitPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole __instance,
        object[] __args)
    {
        if (!IsSubmitKey(__args) ||
            !ReturnByDeathConsoleCommandPatch.IsCustomCommandInInputBuffer(__instance))
        {
            return true;
        }

        // _Input 拦截后不再让原版把未知命令交给普通解析器处理。
        ReturnByDeathConsoleCommandPatch.TryHandleCommand(
            __instance,
            Array.Empty<object>(),
            allowInputBufferFallback: true);
        return false;
    }

    private static bool IsSubmitKey(object? value)
    {
        if (value is InputEventKey key)
        {
            return key.Pressed &&
                   (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter ||
                    key.PhysicalKeycode == Key.Enter || key.PhysicalKeycode == Key.KpEnter);
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (IsSubmitKey(item))
                    return true;
            }
        }

        return false;
    }
}
