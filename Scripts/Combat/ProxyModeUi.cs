// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class ProxyModeState
{
    private static int _enabled;

    public static bool Enabled => Volatile.Read(ref _enabled) != 0;

    public static void Set(bool enabled)
    {
        Volatile.Write(ref _enabled, enabled ? 1 : 0);
        ModLog.Write($"Proxy mode {(enabled ? "enabled" : "disabled") }.");
    }

    public static void Reset() => Volatile.Write(ref _enabled, 0);
}

internal static class ProxyModeToggle
{
    private static CheckButton? _checkButton;
    private static bool _shown;

    public static void Ensure(NRunTimer timer)
    {
        try
        {
            if (AccessTools.Field(typeof(NRunTimer), "_timerLabel")?.GetValue(timer) is not MegaLabel label)
                return;

            var state = RunManager.Instance?.DebugOnlyGetState();
            // 展示条件：死亡回归重放中，且死掉的那条命胜利过至少一场战斗
            // （胜利在前、死亡回归在后）。回归落地即展示，不要求在新时间线
            // 里再赢一场。
            var shouldShow = EncounterJournalStore.ReplayActive &&
                state is not null &&
                AmnesiaState.LastLifeWonABattle();

            if (_checkButton is not null && GodotObject.IsInstanceValid(_checkButton))
            {
                if (!shouldShow)
                {
                    if (ProxyModeState.Enabled)
                        ProxyModeState.Set(false);
                    _checkButton.Visible = false;
                    _shown = false;
                    return;
                }

                if (!_shown)
                {
                    // 刚刚重新展示（新的一次死亡回归/本层首次胜利）：默认开启。
                    ProxyModeState.Set(true);
                    _checkButton.ButtonPressed = true;
                }
                _checkButton.Visible = true;
                _shown = true;
                return;
            }

            if (!shouldShow)
                return;

            // 直接挂到 NRun 根节点，避免时间栏所在容器重新布局时把控件
            // 挤到时间文字右侧；GlobalPosition 明确锚在时间文字左边。
            var parent = NRun.Instance as Node ?? timer;
            if (parent is null)
                return;

            _checkButton = new CheckButton
            {
                Name = "ReturnByDeathProxyMode",
                Text = "代理",
                Size = new Vector2(78f, 32f),
                CustomMinimumSize = new Vector2(78f, 32f),
                ZIndex = 1000,
                ProcessMode = Node.ProcessModeEnum.Always,
                MouseFilter = Control.MouseFilterEnum.Stop,
                FocusMode = Control.FocusModeEnum.All
            };
            _checkButton.Pressed += OnPressed;
            parent.AddChild(_checkButton);
            _checkButton.GlobalPosition = label.GlobalPosition - new Vector2(_checkButton.Size.X + 8f, 0f);
            // 首次展示即默认开启。
            _checkButton.ButtonPressed = true;
            ProxyModeState.Set(true);
            _shown = true;
            ModLog.Write("Proxy mode checkbox attached to the left of the run timer and enabled by default.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Proxy mode toggle setup failed: {exception.Message}");
        }
    }

    private static void OnPressed()
    {
        if (_checkButton is not null && GodotObject.IsInstanceValid(_checkButton))
            ProxyModeState.Set(_checkButton.ButtonPressed);
    }
}

// 顶栏计时器每秒用原版 RunTime 刷新文本；后缀把真实总时长追加在旁边。
