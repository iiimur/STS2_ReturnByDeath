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
            // 展示条件：死亡回归重放中，且本局任意一条已经记录的生命
            // 胜利过至少一场战斗。代理资格一旦解锁就持续保留；不能因为
            // 后续某条命在第一场战斗中死亡而把按钮重新隐藏。
            var shouldShow = EncounterJournalStore.ReplayActive &&
                state is not null &&
                AmnesiaState.HasWonABattleInAnyRecordedLife();

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

// 诅咒预算展示：顶栏计时文字左侧（代理开关更左边）放两个数字——
// 再次死亡将要叠加的“愧疚 a+1 张”和“受伤 b+(a+1)/3 张”，即按当前预算
// 先计算（a++、b += a/3）后的结果，玩家无需自己换算。
internal static class CurseBudgetDisplay
{
    private static Label? _label;
    private const float LabelWidth = 150f;

    public static void Ensure(NRunTimer timer)
    {
        try
        {
            if (AccessTools.Field(typeof(NRunTimer), "_timerLabel")?.GetValue(timer) is not MegaLabel label)
                return;

            if (_label is not null && GodotObject.IsInstanceValid(_label))
            {
                Update();
                return;
            }

            var parent = NRun.Instance as Node ?? timer;
            if (parent is null)
                return;

            _label = new Label
            {
                Name = "ReturnByDeathCurseBudget",
                Size = new Vector2(LabelWidth, 32f),
                ZIndex = 1000,
                ProcessMode = Node.ProcessModeEnum.Always,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            parent.AddChild(_label);
            // 锚在时间文字左边：先让出代理开关的槽位（78 宽 + 8 间距），
            // 代理开关隐藏时留一段空档，避免两个控件随可见性来回跳动。
            _label.GlobalPosition = label.GlobalPosition - new Vector2(LabelWidth + 8f + 78f + 8f, 0f);
            Update();
        }
        catch (Exception exception)
        {
            ModLog.Write($"Curse budget display setup failed: {exception.Message}");
        }
    }

    private static void Update()
    {
        if (_label is null || !GodotObject.IsInstanceValid(_label))
            return;
        var (guilt, injury) = CheckpointStore.CurseBudget.Current;
        // 再次死亡的惩罚：a+1 张愧疚；受伤施加值为累进后的 b += (a+1)/3。
        _label.Text = $"愧疚 {guilt + 1} 受伤 {injury + (guilt + 1) / 3}";
    }
}

// 顶栏计时器每秒用原版 RunTime 刷新文本；后缀把真实总时长追加在旁边。
