// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 「愤怒」IF 线的结局触发演出：与傲慢/怠惰/强欲三个结局的触发图完全同款——
// 在蕾姆事件所选稀有牌被主动移出牌组、进入愤怒线时，播放统一结局音效并
// 淡入羽化触发图，点击屏幕下半部分淡出。
internal static class WrathEndingOverlay
{
    private const string ImageFileName = "愤怒结局触发-羽化.png";
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
                throw new InvalidOperationException("SceneTree root is unavailable for the wrath ending overlay.");

            var imagePath = ModAssetPaths.Image(ImageFileName);
            var sourceImage = Image.LoadFromFile(imagePath);
            if (sourceImage is null)
                throw new FileNotFoundException($"Wrath ending image could not be loaded: {imagePath}", imagePath);

            var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
            layer = new CanvasLayer
            {
                Name = "ReturnByDeathWrathEnding",
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

            // 与其余三个结局一致：用屏幕坐标的 Sprite2D 以视口中心为锚点，
            // 按图片原始尺寸等比缩放，避免大图偏到角落。
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
            ModLog.Write("Wrath ending overlay triggered after entering the wrath IF route.");
        }
        catch (Exception exception)
        {
            if (layer is not null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            Volatile.Write(ref _active, 0);
            ModLog.Write($"Wrath ending overlay failed: {exception}");
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
            ModLog.Write($"Wrath ending fade-out failed: {exception.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            Volatile.Write(ref _fadingOut, 0);
            Volatile.Write(ref _active, 0);
            ModLog.Write("Wrath ending overlay closed.");
        }
    }
}
