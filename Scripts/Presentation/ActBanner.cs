// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 原版 NActBanner 在进入每层（MapRoom._Ready 与 NMapScreen 层首动画）时播放
// “阶段一 / 密林”之类的文字报幕。死亡回归重载存档会重新进入层首房间，报幕
// 因此反复出现。若当前层发生过死亡回归，则跳过原生 _Ready，改用 Re0 标题图，
// 并复刻原生 AnimateVfx 的出现-停留-淡出节奏与位置（居中、暗色底幕）。
[HarmonyPatch(typeof(NActBanner), "_Ready")]
internal static class ActBannerReplacePatch
{
    private const string ImageFileName = "Re0标题图.png";

    // 标题图占屏幕的显示比例，以及相对屏幕中心的整体上移量（按屏高的比例）。
    private const float ImageSizeFactor = 0.5f;
    private const float ImageUpwardShiftFactor = 0.2f;

    private static System.Reflection.FieldInfo? _actIndexField;

    [HarmonyPrefix]
    private static bool Prefix(NActBanner __instance)
    {
        try
        {
            _actIndexField ??= AccessTools.Field(typeof(NActBanner), "_actIndex");
            var actIndex = _actIndexField?.GetValue(__instance) as int?;
            if (!RouteState.ConsumeSpecialBanner())
                return true;

            _ = ReplaceWithImageAsync(__instance);
            ModLog.Write($"Act banner replaced with Re0 title image (act {(actIndex ?? -1) + 1}).");
            return false;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Act banner replacement failed; falling back to native banner: {exception}");
            return true;
        }
    }

    private static async Task ReplaceWithImageAsync(NActBanner banner)
    {
        // 清理原生报幕节点，尤其移除黑色横条，避免与自定义标题图叠加。
        foreach (var nodeName in new[] { "ActNumber", "ActName" })
        {
            var label = banner.GetNodeOrNull<Control>(nodeName);
            if (label is not null)
                label.QueueFree();
        }
        var backdrop = banner.GetNodeOrNull<ColorRect>("%Banner");
        if (backdrop is not null)
            backdrop.QueueFree();

        var imagePath = ModAssetPaths.Image(ImageFileName);
        var sourceImage = Image.LoadFromFile(imagePath);
        if (sourceImage is null)
            throw new FileNotFoundException($"Re0 title image could not be loaded: {imagePath}", imagePath);
        var image = new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(sourceImage),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0f)
        };
        banner.AddChild(image);
        image.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // 自定义标题图以原生报幕的显示区域为基准，大小为 50%，整体上移 20%。
        image.PivotOffset = banner.Size / 2f;
        image.Scale = new Vector2(ImageSizeFactor, ImageSizeFactor);
        image.Position = new Vector2(0f, -banner.Size.Y * ImageUpwardShiftFactor);
        var fast = SaveManager.Instance.PrefsSave.FastMode == MegaCrit.Sts2.Core.Settings.FastModeType.Fast;
        if (backdrop is not null)
            backdrop.Modulate = StsColors.transparentBlack;
        var tween = banner.CreateTween().SetParallel();
        // 淡入加快：底幕 0.25s，标题图 0.5s；停留时间再增加 2s；淡出 2s。
        if (backdrop is not null)
            tween.TweenProperty(backdrop, "modulate:a", 0.25f, 0.25).SetDelay(0.25);
        tween.TweenProperty(image, "modulate:a", 1f, 0.5).SetDelay(0.125);
        tween.Chain();
        tween.TweenInterval(fast ? 3.0 : 6.0);
        tween.Chain();
        tween.TweenProperty(banner, "modulate:a", 0f, 2.0)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Quad);

        try
        {
            await MegaCrit.Sts2.Core.Nodes.GodotExtensions.TweenHelper.AwaitFinished(tween, banner);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Re0 act banner animation failed: {exception.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(banner) && banner.IsInsideTree())
                banner.QueueFree();
        }
    }
}
