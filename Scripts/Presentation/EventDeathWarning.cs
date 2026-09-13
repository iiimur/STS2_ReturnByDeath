// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class EventDeathWarningInterlude
{
    private const string AudioFileName = "爱你自己.wav";
    private const double SubtitleDelaySeconds = 0.3d;
    private static int _active;

    public static bool ShouldPlay(EventOption option, Player? player)
    {
        // 怠惰线中的事件生命代价已归零；不播放原本针对必死选项的警告演出。
        if (SlothRouteRules.IsInEventRoom || player is null || Volatile.Read(ref _active) != 0)
            return false;

        // 艾姬多娜的“强欲之心”选项：按设定理应致命所以保留红字警告，
        // 但实际暗中不扣血，因此“爱你自己”的必死演出在这里截断。
        if (option.TextKey == "RBD_FLOWER.HEART")
            return false;

        try
        {
            return option.WillKillPlayer?.Invoke(player) == true;
        }
        catch (Exception exception)
        {
            // 绝不能因演出判定故障阻断原版事件；记录后让原流程继续。
            ModLog.Write($"Event death-warning check failed: {exception.Message}");
            return false;
        }
    }

    public static Task PlayThenContinueAsync(Task nativeBeforeChoice, EventOption option)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
            return nativeBeforeChoice;

        return PlayThenContinueInternalAsync(nativeBeforeChoice, option);
    }

    private static async Task PlayThenContinueInternalAsync(Task nativeBeforeChoice, EventOption option)
    {
        CanvasLayer? layer = null;
        CanvasLayer? nightmareLayer = null;
        var nightmareVfxAttached = false;
        AudioStreamPlayer? player = null;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
            {
                await nativeBeforeChoice;
                return;
            }

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            layer = new CanvasLayer
            {
                Name = "ReturnByDeathEventDeathWarning",
                Layer = 10000,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(layer);

            layer.AddChild(new ColorRect
            {
                Color = Colors.Black,
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop
            });

            // 使用系统默认的中文无衬线字体（macOS 上即黑体风格），以白字保证
            // 在纯黑背景中可读；描边让字幕在随后特效的亮部也保持清晰。
            var subtitle = new Label
            {
                Text = "你要更加爱惜自己",
                Position = new Vector2(0f, Math.Max(0f, viewportSize.Y - 180f)),
                Size = new Vector2(viewportSize.X, 100f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            subtitle.AddThemeFontSizeOverride("font_size", 50);
            subtitle.AddThemeColorOverride("font_color", Colors.White);
            subtitle.AddThemeColorOverride("font_outline_color", Colors.Black);
            subtitle.AddThemeConstantOverride("outline_size", 8);
            layer.AddChild(subtitle);
            _ = ShowSubtitleAfterDelayAsync(tree, layer, subtitle);

            // 夜魇特效放在独立、比黑屏更高的 CanvasLayer 中。它和黑屏同一时刻
            // 建立，因此黑幕期间仍能清楚看到完整的环屏演出。
            nightmareLayer = new CanvasLayer
            {
                Name = "ReturnByDeathEventDeathWarningNightmare",
                Layer = 10001,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(nightmareLayer);
            var nightmareHost = new Control
            {
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            nightmareLayer.AddChild(nightmareHost);

            var nightmareVfx = NNightmareHandsVfx.Create();
            if (nightmareVfx is not null)
            {
                nightmareVfx.TreeExited += () =>
                {
                    if (GodotObject.IsInstanceValid(nightmareLayer))
                        nightmareLayer.QueueFree();
                };
                nightmareHost.AddChild(nightmareVfx);
                nightmareVfxAttached = true;
            }
            else
            {
                ModLog.Write("Event death-warning could not create Nightmare VFX.");
            }

            var audioPath = ModAssetPaths.Audio(AudioFileName);
            var stream = AudioStreamWav.LoadFromFile(audioPath);
            if (stream is null)
            {
                ModLog.Write($"Event death-warning WAV could not be loaded: {audioPath}");
            }
            else
            {
                player = new AudioStreamPlayer
                {
                    Name = "ReturnByDeathEventDeathWarningAudio",
                    Stream = stream,
                    Bus = "Master",
                    ProcessMode = Node.ProcessModeEnum.Always
                };
                tree.Root.AddChild(player);
                player.Play();
                ModLog.Write($"Event death-warning started for option '{option.TextKey}'.");
                await player.ToSignal(player, AudioStreamPlayer.SignalName.Finished);
            }

            // 字幕和黑屏在音频结束时一同退场；夜魇特效从黑屏开始播放，
            // 并自行结束，不受黑屏层销毁影响。
            if (layer is not null && GodotObject.IsInstanceValid(layer))
            {
                layer.QueueFree();
                layer = null;
            }

            await nativeBeforeChoice;
        }
        catch (Exception exception)
        {
            // 演出失败时仍放行原版选择，不能让玩家卡在问号事件里。
            ModLog.Write($"Event death-warning interlude failed: {exception}");
            await nativeBeforeChoice;
        }
        finally
        {
            if (player is not null && GodotObject.IsInstanceValid(player))
                player.QueueFree();
            if (layer is not null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            if (!nightmareVfxAttached && nightmareLayer is not null && GodotObject.IsInstanceValid(nightmareLayer))
                nightmareLayer.QueueFree();
            Volatile.Write(ref _active, 0);
        }
    }

    // 字幕仍是黑屏层的子节点：延迟只影响出现时机，结束时则会随黑屏一并退场。
    private static async Task ShowSubtitleAfterDelayAsync(SceneTree tree, CanvasLayer layer, Label subtitle)
    {
        try
        {
            var timer = tree.CreateTimer(SubtitleDelaySeconds);
            await tree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
            if (GodotObject.IsInstanceValid(layer) && GodotObject.IsInstanceValid(subtitle))
                subtitle.Visible = true;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Event death-warning subtitle delay failed: {exception.Message}");
        }
    }
}

// BeforeOptionChosen 正位于 EventOption.OnChosen 之前：把它的 Task 包起来，
// 能完整保留原版按钮禁用、事件历史和选项效果，只延后实际死亡结算。
[HarmonyPatch(typeof(NEventRoom), "BeforeOptionChosen", new[] { typeof(EventOption) })]
internal static class EventRoomDeathWarningPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEventRoom __instance, EventOption option, ref Task __result)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        var player = state?.Players.FirstOrDefault();
        if (!EventDeathWarningInterlude.ShouldPlay(option, player))
            return;

        __result = EventDeathWarningInterlude.PlayThenContinueAsync(__result, option);
    }
}
