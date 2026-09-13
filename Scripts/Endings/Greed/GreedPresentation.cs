// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 「强欲」IF 线的结局触发演出：与傲慢/怠惰两个结局的触发图完全同款——
// 在艾姬多娜事件拿到强欲之心时，播放统一结局音效并淡入羽化触发图，
// 点击屏幕下半部分淡出。
internal static class GreedEndingOverlay
{
    private const string ImageFileName = "强欲结局触发-羽化.png";
    private const float FadeDurationSeconds = 0.35f;
    private static int _active;
    private static int _fadingOut;

    public static void TryShow()
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
            return;

        CanvasLayer? layer = null;
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null)
                throw new InvalidOperationException("SceneTree root is unavailable for the greed ending overlay.");

            var imagePath = ModAssetPaths.Image(ImageFileName);
            var sourceImage = Image.LoadFromFile(imagePath);
            if (sourceImage is null)
                throw new FileNotFoundException($"Greed ending image could not be loaded: {imagePath}", imagePath);

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            layer = new CanvasLayer
            {
                Name = "ReturnByDeathGreedEnding",
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
            ModLog.Write("Greed ending overlay triggered after obtaining the Heart of Greed.");
        }
        catch (Exception exception)
        {
            if (layer is not null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            Volatile.Write(ref _active, 0);
            ModLog.Write($"Greed ending overlay failed: {exception}");
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
            ModLog.Write($"Greed ending fade-out failed: {exception.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            Volatile.Write(ref _fadingOut, 0);
            Volatile.Write(ref _active, 0);
            ModLog.Write("Greed ending overlay closed.");
        }
    }
}
