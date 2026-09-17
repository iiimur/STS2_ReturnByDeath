// 艾姬多娜入口的前进拦截提示：复用原生商店“购买失败”时的商人对话气泡，
// 但不显示商店随机台词，而是保留原生随机位置/动画并显示指定文案。

using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace ReturnByDeath;

internal static class EchidnaTravelNotice
{
    private const string NoticeText = "你要先接受试炼才能继续前行。";
    private const string DialogueName = "ReturnByDeathEchidnaTravelNotice";
    private static NMerchantDialogue? _dialogue;
    private static CanvasLayer? _layer;

    public static void Show()
    {
        ModLog.Write("Echidna travel notice requested.");
        _ = TaskHelper.RunSafely(ShowAsync());
    }

    public static void Reset()
    {
        if (_layer is not null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _layer = null;
        _dialogue = null;
    }

    private static async Task ShowAsync()
    {
        try
        {
            var dialogue = await EnsureDialogueAsync();
            if (dialogue is null || !GodotObject.IsInstanceValid(dialogue))
                return;

            // 直接走原生商店气泡的 ShowRandom 流程（包含原生淡入、气泡动画和音效），
            // 保留原生随机位置。
            var showRandom = AccessTools.Method(typeof(NMerchantDialogue), "ShowRandom")
                ?? throw new MissingMethodException(typeof(NMerchantDialogue).FullName, "ShowRandom");
            showRandom.Invoke(dialogue, new object[]
            {
                // ShowRandom 会先解析 LocString；“merchant”不是本版本的表名，
                // 用必定存在的事件词条作占位，随后立即覆盖成提示正文。
                new List<LocString> { new("events", "COLOSSAL_FLOWER.pages.INITIAL.description") }
            });
            SetFixedText(dialogue);

            // ShowRandom 原生流程会在约 1 秒内把透明度淡到 0，但节点必须保持
            // Visible=true；这样下一次点击可以直接再次播放原生气泡动画。
        }
        catch (Exception exception)
        {
            ModLog.Write($"Echidna travel notice failed: {exception}");
        }
    }

    private static async Task<NMerchantDialogue?> EnsureDialogueAsync()
    {
        if (_dialogue is not null && GodotObject.IsInstanceValid(_dialogue))
            return _dialogue;

        var map = NMapScreen.Instance;
        if (map is null || !GodotObject.IsInstanceValid(map))
            return null;

        // 商店场景本身包含完整的 NMerchantDialogue 及其气泡贴图；
        // 从该场景提取对话节点后脱离商店根节点，避免创建可交互商店。
        var scenePath = NMerchantRoom.AssetPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scenePath))
            return null;

        var packed = ResourceLoader.Load<PackedScene>(scenePath);
        if (packed is null)
        {
            ModLog.Write($"Echidna travel notice could not load merchant scene: {scenePath}");
            return null;
        }

        var template = packed.Instantiate<NMerchantRoom>();
        var dialogue = FindNode<NMerchantDialogue>(template);
        if (dialogue is null || !GodotObject.IsInstanceValid(dialogue))
        {
            template.QueueFree();
            ModLog.Write("Echidna travel notice could not find NMerchantDialogue in merchant scene.");
            return null;
        }

        dialogue.Name = DialogueName;
        dialogue.ProcessMode = Node.ProcessModeEnum.Always;
        dialogue.ZIndex = 0;
        dialogue.TopLevel = true;
        // 先从临时商店根节点摘出对话气泡，再放入独立高层 CanvasLayer；
        // 这样不会触发 NMerchantRoom 的 _Ready，也不会被地图节点/遮罩盖住。
        dialogue.GetParent()?.RemoveChild(dialogue);
        var root = map.GetTree().Root;
        var layer = new CanvasLayer
        {
            Name = "ReturnByDeathEchidnaTravelNoticeLayer",
            Layer = 2000,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        root.AddChild(layer);
        layer.AddChild(dialogue);
        _layer = layer;
        template.QueueFree();
        _dialogue = dialogue;
        // 等待气泡自己的 _Ready，确保其内部标签和动画节点已绑定。
        await AwaitProcessFrame();
        return dialogue;
    }

    private static T? FindNode<T>(Node root) where T : Node
    {
        if (root is T match)
            return match;

        foreach (var child in root.GetChildren())
        {
            if (child is not Node node)
                continue;
            var result = FindNode<T>(node);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static void SetFixedText(NMerchantDialogue dialogue)
    {
        var label = AccessTools.Field(typeof(NMerchantDialogue), "_label")?
            .GetValue(dialogue) as MegaRichTextLabel;
        if (label is null)
            return;

        label.BbcodeEnabled = false;
        label.SetTextAutoSize(NoticeText);
    }

    internal static bool IsTracked(NMerchantDialogue dialogue) =>
        ReferenceEquals(dialogue, _dialogue) && GodotObject.IsInstanceValid(dialogue);

    private static async Task AwaitProcessFrame()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

}
