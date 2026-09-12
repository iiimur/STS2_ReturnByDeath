// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class PrideEndingOverlay
{
    private const string ImageFileName = "傲慢结局触发-羽化.png";
    private const float FadeDurationSeconds = 0.35f;
    private static int _active;
    private static int _fadingOut;

    public static void TryShow()
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
            return;

        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable for the pride ending overlay.");

            var imagePath = ModAssetPaths.Image(ImageFileName);
            var sourceImage = Image.LoadFromFile(imagePath);
            if (sourceImage is null)
                throw new FileNotFoundException($"Pride ending image could not be loaded: {imagePath}", imagePath);

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            var layer = new CanvasLayer
            {
                Name = "ReturnByDeathPrideEnding",
                Layer = 10002,
                ProcessMode = Node.ProcessModeEnum.Always
            };
            tree.Root.AddChild(layer);

            var dim = new ColorRect
            {
                Color = Colors.Black,
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = new Color(1f, 1f, 1f, 0f)
            };
            layer.AddChild(dim);

            // CanvasLayer 下的 TextureRect 会受控件布局和窗口缩放共同影响，曾导致
            // 大图偏到右下角。改用屏幕坐标的 Sprite2D：原点就是屏幕左上角，
            // 直接以视口中心为锚点并按图片原始尺寸等比缩放，始终完整居中显示。
            var imageTexture = ImageTexture.CreateFromImage(sourceImage);
            var artScale = MathF.Min(
                viewportSize.X / sourceImage.GetWidth(),
                viewportSize.Y / sourceImage.GetHeight());
            var art = new Sprite2D
            {
                Texture = imageTexture,
                Centered = true,
                Position = viewportSize / 2f,
                Scale = new Vector2(artScale, artScale),
                ZIndex = 1,
                Modulate = new Color(1f, 1f, 1f, 0f)
            };
            layer.AddChild(art);

            // 覆盖整个屏幕以阻止演出期间误操作；只有下半屏点击会关闭结局图。
            var inputBlocker = new Control
            {
                Position = Vector2.Zero,
                Size = viewportSize,
                MouseFilter = Control.MouseFilterEnum.Stop,
                FocusMode = Control.FocusModeEnum.All
            };
            inputBlocker.GuiInput += input =>
            {
                if (input is InputEventMouseButton
                    {
                        ButtonIndex: MouseButton.Left,
                        Pressed: true,
                        Position: var position
                    } && position.Y >= viewportSize.Y / 2f)
                {
                    BeginFadeOut(layer, dim, art);
                }
            };
            layer.AddChild(inputBlocker);

            var fadeIn = art.CreateTween();
            fadeIn.SetParallel(true);
            fadeIn.TweenProperty(dim, new NodePath("modulate:a"), 0.62f, FadeDurationSeconds);
            fadeIn.TweenProperty(art, new NodePath("modulate:a"), 1f, FadeDurationSeconds);
            // 结局触发图开始淡入的同时播放统一的结局进入音效。
            CosmeticAudio.TryPlay("进入结局.wav");
            ModLog.Write("Pride ending triggered because Reinhard was removed from the deck by another effect.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _active, 0);
            ModLog.Write($"Pride ending overlay failed: {exception}");
        }
    }

    private static void BeginFadeOut(CanvasLayer layer, ColorRect dim, Sprite2D art)
    {
        if (Interlocked.Exchange(ref _fadingOut, 1) != 0)
            return;

        _ = FadeOutAsync(layer, dim, art);
    }

    private static async Task FadeOutAsync(CanvasLayer layer, ColorRect dim, Sprite2D art)
    {
        try
        {
            var fadeOut = art.CreateTween();
            fadeOut.SetParallel(true);
            fadeOut.TweenProperty(dim, new NodePath("modulate:a"), 0f, FadeDurationSeconds);
            fadeOut.TweenProperty(art, new NodePath("modulate:a"), 0f, FadeDurationSeconds);
            await art.ToSignal(fadeOut, Tween.SignalName.Finished);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Pride ending fade-out failed: {exception.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            Volatile.Write(ref _fadingOut, 0);
            Volatile.Write(ref _active, 0);
            ModLog.Write("Pride ending overlay closed.");
        }
    }
}

// 怠惰 IF 线进入时的顶层演出：图片和背景淡入，点击屏幕下半部分后一同淡出。
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook), nameof(MegaCrit.Sts2.Core.Hooks.Hook.BeforeHandDraw))]
internal static class PrideFinalBossOpeningPatch
{
    [HarmonyPostfix]
    private static void Postfix(ICombatState combatState, Player player,
        PlayerChoiceContext playerChoiceContext, ref Task __result)
    {
        if (!PrideFinalBossOpening.TryBegin(combatState))
            return;

        var originalHookTask = __result;
        __result = PrideFinalBossOpening.PlayAsync(originalHookTask, combatState, player);
    }
}

internal static class PrideFinalBossOpening
{
    private const double SpeechDurationSeconds = 3.0;
    private static int _played;

    public static void ResetForNewRun() => Volatile.Write(ref _played, 0);

    public static bool TryBegin(ICombatState combatState)
    {
        if (!RouteState.IsPrideRoute || combatState is null)
            return false;

        // Act 索引从 0 开始，2 即第三层；RoomType.Boss 可排除普通战斗。
        if (combatState.RunState.CurrentActIndex != 2 ||
            combatState.Encounter?.RoomType != RoomType.Boss)
            return false;

        // 高进阶（Ascension 10+）的第三层可能有两个 Boss。
        // ActMap.SecondBossMapPoint 为空时是普通模式，此时当前 Boss 就是最终 Boss；
        // 非空时只允许第二个 Boss 触发这段战前演出。
        var secondBossMapPoint = combatState.RunState.Map?.SecondBossMapPoint;
        if (secondBossMapPoint is not null &&
            !IsCurrentMapPoint(combatState.RunState.CurrentMapPoint, secondBossMapPoint))
        {
            ModLog.Write("Pride final-boss opening skipped for the first Boss in Double Boss mode.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _played, 1, 0) != 0)
            return false;

        ModLog.Write("Pride final-boss opening dialogue reserved.");
        return true;
    }

    private static bool IsCurrentMapPoint(
        MegaCrit.Sts2.Core.Map.MapPoint? current,
        MegaCrit.Sts2.Core.Map.MapPoint target) =>
        current is not null && (ReferenceEquals(current, target) || current.Equals(target));

    public static async Task PlayAsync(Task originalHookTask, ICombatState combatState, Player player)
    {
        try
        {
            // 保留原版 BeforeHandDraw 的所有行为，再在发牌前插入对白。
            await originalHookTask;

            await PlaySpeechAsync(
                player.Creature,
                "莱因哈鲁特·范·阿斯特雷亚……");

            var boss = combatState.Enemies.FirstOrDefault(enemy =>
                enemy.IsAlive && enemy.Monster is not null);
            if (boss is not null)
            {
                await PlaySpeechAsync(
                    boss,
                    "看来我没必要自我介绍了呢。");
            }
            else
            {
                ModLog.Write("Pride final-boss dialogue could not find a living boss creature.");
            }

            ModLog.Write("Pride final-boss opening dialogue completed; normal draw flow resumed.");
        }
        catch (Exception exception)
        {
            // 对白失败不能阻断最终 Boss 战。异常时直接恢复原版发牌流程。
            ModLog.Write($"Pride final-boss opening dialogue failed: {exception}");
        }
    }

    private static async Task PlaySpeechAsync(Creature speaker, string text)
    {
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom is null || !GodotObject.IsInstanceValid(combatRoom))
        {
            ModLog.Write($"Pride speech skipped because combat room is unavailable: {text}");
            return;
        }

        var bubble = NSpeechBubbleVfx.Create(
            text,
            speaker,
            SpeechDurationSeconds,
            VfxColor.White);
        if (bubble is null)
        {
            ModLog.Write($"Pride speech bubble creation failed: {text}");
            return;
        }

        combatRoom.CombatVfxContainer.AddChild(bubble);
        await Cmd.Wait((float)SpeechDurationSeconds, false);

        if (GodotObject.IsInstanceValid(bubble))
            bubble.QueueFree();
    }
}
