// 愤怒 IF 线的战斗视觉：只将敌方 Creature 的美术 Body 转为灰度；
// 血条、护甲、意图、悬浮文字、玩家与战斗背景保持原色。

using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds;

namespace ReturnByDeath;

internal static class WrathMonsterVisuals
{
    private static readonly string[] SoulNexusNativeNormalSlots = ["glowie", "glowie2"];

    private const string GrayscaleShaderCode = """
        shader_type canvas_item;
        render_mode blend_premul_alpha;

        void fragment() {
            vec4 source = texture(TEXTURE, UV) * COLOR;
            float luminance = dot(source.rgb, vec3(0.299, 0.587, 0.114));
            COLOR = vec4(vec3(luminance), source.a);
        }
        """;

    private const string AdditiveGrayscaleShaderCode = """
        shader_type canvas_item;
        render_mode blend_add;

        void fragment() {
            vec4 source = texture(TEXTURE, UV) * COLOR;
            float luminance = dot(source.rgb, vec3(0.299, 0.587, 0.114));
            COLOR = vec4(vec3(luminance), source.a);
        }
        """;

    private const string MultiplyGrayscaleShaderCode = """
        shader_type canvas_item;
        render_mode blend_mul;

        void fragment() {
            vec4 source = texture(TEXTURE, UV) * COLOR;
            float luminance = dot(source.rgb, vec3(0.299, 0.587, 0.114));
            // multiply 混合的中性颜色是白色而不是透明黑。若直接输出
            // vec4(gray, alpha)，贴图透明区的 RGB=0 会把背景乘成黑色方框。
            // 用 alpha 在中性白与灰度纹理间插值，既保留半透明边缘，又让
            // 完全透明区域对背景没有任何影响。
            vec3 multiply_factor = mix(vec3(1.0), vec3(luminance), source.a);
            COLOR = vec4(multiply_factor, 1.0);
        }
        """;

    private static Shader? _grayscaleShader;
    private static Shader? _additiveGrayscaleShader;
    private static Shader? _multiplyGrayscaleShader;

    private static Shader GrayscaleShader => _grayscaleShader ??= new Shader
    {
        Code = GrayscaleShaderCode
    };

    private static Shader AdditiveGrayscaleShader => _additiveGrayscaleShader ??= new Shader
    {
        Code = AdditiveGrayscaleShaderCode
    };

    private static Shader MultiplyGrayscaleShader => _multiplyGrayscaleShader ??= new Shader
    {
        Code = MultiplyGrayscaleShaderCode
    };

    public static void ApplyToAllEnemies(NCombatRoom? room)
    {
        if (!RouteState.IsWrathRoute || room is null || !GodotObject.IsInstanceValid(room))
            return;

        try
        {
            foreach (var creatureNode in room.CreatureNodes.ToArray())
                ApplyToEnemy(creatureNode);
        }
        catch (Exception exception)
        {
            // 纯视觉效果绝不能中断原版战斗初始化。
            ModLog.Write($"Wrath route grayscale pass failed: {exception}");
        }
    }

    public static void ApplyToEnemy(NCreature? creatureNode)
    {
        if (!RouteState.IsWrathRoute || creatureNode is null ||
            !GodotObject.IsInstanceValid(creatureNode) || creatureNode.Entity?.IsEnemy != true ||
            creatureNode.Entity.Monster is MegaCrit.Sts2.Core.Models.Monsters.Architect)
        {
            return;
        }

        try
        {
            var visuals = creatureNode.Visuals;
            var body = visuals?.GetCurrentBody() ?? creatureNode.Body;
            if (body is null || !GodotObject.IsInstanceValid(body))
                return;

            // 千足虫前、中段用覆盖整段身体的大型 additive FX（segment_1_fx、
            // seg_2_fx_top/bottom）；灵魂枢纽则有一张随骨骼旋转的大型 glowie
            // 光环。把这些特殊混合层替换为通用灰度材质会暴露整张矩形贴图，
            // 因此只将它们的普通主体材质转灰，保留特殊混合层的原生渲染。
            var preserveSpecialBlendMaterials = creatureNode.Entity.Monster is
                MegaCrit.Sts2.Core.Models.Monsters.DecimillipedeSegmentFront or
                MegaCrit.Sts2.Core.Models.Monsters.DecimillipedeSegmentMiddle or
                MegaCrit.Sts2.Core.Models.Monsters.SoulNexus;
            var preservedNormalSlots = creatureNode.Entity.Monster is
                MegaCrit.Sts2.Core.Models.Monsters.SoulNexus
                    ? SoulNexusNativeNormalSlots
                    : null;
            var spineChanged = TryApplyToSpine(
                visuals?.SpineBody,
                applySpecialBlendMaterials: !preserveSpecialBlendMaterials,
                preservedNormalSlots: preservedNormalSlots);

            // Spine 子树里的法杖、粒子和闪光拥有自己的专用材质。主骨骼已经
            // 通过 Spine 自己的混合材质整体转灰，不能再递归覆盖这些特效节点。
            // 非 Spine 怪物才使用逐个贴图节点的兼容路径。
            var canvasItemsChanged = spineChanged ? 0 : ApplyRecursively(body);
            ModLog.Write(
                $"Wrath route grayscale applied to {creatureNode.Entity.ModelId}: " +
                $"MegaSpine={spineChanged}, CanvasItem={canvasItemsChanged}, " +
                $"SpecialBlendMaterials={!preserveSpecialBlendMaterials}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Wrath route could not grayscale {creatureNode.Entity?.ModelId}: {exception}");
        }
    }

    private static ShaderMaterial CreateGrayscaleMaterial() => new()
    {
        Shader = GrayscaleShader
    };

    private static bool TryApplyToSpine(
        MegaSprite? spineBody,
        bool applySpecialBlendMaterials = true,
        IReadOnlyList<string>? preservedNormalSlots = null)
    {
        if (spineBody?.BoundObject is not { } boundSpine || !GodotObject.IsInstanceValid(boundSpine))
            return false;

        // Spine 按 slot 分为 normal/additive/multiply/screen 四套材质。只改 normal
        // 会让法杖光效等特殊 slot 保留颜色；把 additive/multiply 也换成对应混合
        // 方式的灰度材质，同时保留 screen 的原生材质，避免改变其特殊屏幕混合。
        if (preservedNormalSlots is { Count: > 0 })
            PreserveNormalSlotMaterials(spineBody, boundSpine, preservedNormalSlots);
        spineBody.SetNormalMaterial(CreateGrayscaleMaterial());
        if (applySpecialBlendMaterials)
        {
            boundSpine.Set("additive_material", new ShaderMaterial { Shader = AdditiveGrayscaleShader });
            boundSpine.Set("multiply_material", new ShaderMaterial { Shader = MultiplyGrayscaleShader });
        }
        return true;
    }

    private static void PreserveNormalSlotMaterials(
        MegaSprite spineBody,
        GodotObject boundSpine,
        IReadOnlyList<string> slotNames)
    {
        if (boundSpine is not Node spineNode)
        {
            ModLog.Write("Wrath route could not preserve Spine slot materials: SpineSprite was not a Node.");
            return;
        }

        // SpineSprite 没有显式 normal_material 时，GetNormalMaterial() 会返回 null，
        // 但这代表它正在使用原生的默认 CanvasItem normal 混合，并不是材质缺失。
        // 给需要保留原样的槽位创建等价的默认材质，使其不再继承随后设置到
        // SpineSprite 全局 normal_material 上的灰度 Shader。
        var originalNormalMaterial = spineBody.GetNormalMaterial() ?? new CanvasItemMaterial();

        foreach (var slotName in slotNames)
        {
            var overrideNodeName = $"ReturnByDeathNativeMaterial_{slotName}";
            if (spineNode.GetChildren().Any(child =>
                    string.Equals(child.Name.ToString(), overrideNodeName, StringComparison.Ordinal)))
            {
                continue;
            }

            var slotObject = ClassDB.Instantiate("SpineSlotNode").AsGodotObject();
            if (slotObject is not Node slotNode)
            {
                slotObject?.Dispose();
                ModLog.Write($"Wrath route could not create the native material override for Spine slot {slotName}.");
                continue;
            }

            slotNode.Name = overrideNodeName;
            slotObject.Set("slot_name", slotName);
            slotObject.Set("normal_material", originalNormalMaterial);
            spineNode.AddChild(slotNode);
            ModLog.Write($"Wrath route preserved native rendering for Spine slot {slotName}.");
        }
    }

    public static void ApplyToKaiserCrab(NKaiserCrabBossBackground? background)
    {
        if (!RouteState.IsWrathRoute || background is null || !GodotObject.IsInstanceValid(background))
            return;

        try
        {
            var spine = AccessTools.Field(typeof(NKaiserCrabBossBackground), "_animController")
                ?.GetValue(background) as MegaSprite;
            var changed = TryApplyToSpine(spine);
            ModLog.Write($"Wrath route Kaiser Crab grayscale applied: MegaSpine={changed}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Wrath route could not grayscale Kaiser Crab: {exception}");
        }
    }

    private static int ApplyRecursively(Node node)
    {
        var changed = 0;
        if (IsTextureDrawingNode(node) &&
            node is CanvasItem canvasItem && GodotObject.IsInstanceValid(canvasItem))
        {
            // 每个绘制节点各用一份材质，避免原版怪物后续修改某个材质参数时
            // 连带影响其他怪物；不设置 UseParentMaterial，因此只作用于 Body 子树。
            canvasItem.Material = CreateGrayscaleMaterial();
            changed++;
        }

        foreach (var child in node.GetChildren())
            changed += ApplyRecursively(child);

        return changed;
    }

    private static bool IsTextureDrawingNode(Node node) => node is
        Sprite2D or AnimatedSprite2D or Polygon2D or MeshInstance2D or
        MultiMeshInstance2D or TextureRect or NinePatchRect;
}

// 原版会在这里给所有敌人设置随机缩放与色相。后缀处理确保灰度材质不会
// 被随后进行的原版色相初始化覆盖。
[HarmonyPatch(typeof(NCombatRoom), "RandomizeEnemyScalesAndHues")]
internal static class WrathInitialEnemyGrayscalePatch
{
    [HarmonyPostfix]
    private static void Postfix(NCombatRoom __instance) =>
        WrathMonsterVisuals.ApplyToAllEnemies(__instance);
}

// 部分战斗会在开场后召唤或复活新怪物；AddCreature 返回时节点已挂入
// CreatureNodes，单独给该实例补上灰度材质。
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature), new[] { typeof(Creature) })]
internal static class WrathAddedEnemyGrayscalePatch
{
    [HarmonyPostfix]
    private static void Postfix(NCombatRoom __instance, Creature creature)
    {
        if (!RouteState.IsWrathRoute || creature?.IsEnemy != true)
            return;

        try
        {
            WrathMonsterVisuals.ApplyToEnemy(__instance.GetCreatureNode(creature));
        }
        catch (Exception exception)
        {
            ModLog.Write($"Wrath route could not find the added enemy node for {creature.ModelId}: {exception.Message}");
        }
    }
}

// 凯撒螃蟹的 Crusher/Rocket Creature 只是血条和选中框；真正的 Boss 身体是
// 战斗背景中的独立 MegaSpine。它 Ready 后直接处理其 _animController。
[HarmonyPatch(typeof(NKaiserCrabBossBackground), "_Ready")]
internal static class WrathKaiserCrabGrayscalePatch
{
    [HarmonyPostfix]
    private static void Postfix(NKaiserCrabBossBackground __instance) =>
        WrathMonsterVisuals.ApplyToKaiserCrab(__instance);
}
