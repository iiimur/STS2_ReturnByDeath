// 从原 ReturnByDeath.cs 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

internal static class SlothRouteRules
{
    public static bool IsActive => RouteState.IsSlothRoute;

    // 建筑师是本局真正的结局。即使处在怠惰线，也必须保留其原版遭遇、演出与
    // 死亡结算，否则游戏无法完成本局。
    public static bool IsArchitectFinale => ArchitectFinaleState.IsActive;

    public static bool ShouldSuppressEventHealthLoss => IsActive && !IsArchitectFinale;

    public static bool IsInEventRoom
    {
        get
        {
            if (!ShouldSuppressEventHealthLoss)
                return false;

            return RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is EventRoom;
        }
    }

    public static void DisableControl(Control? control)
    {
        if (!IsActive || control is null || !GodotObject.IsInstanceValid(control))
            return;

        try
        {
            // NPauseMenuButton 与 NAbandonRunButton 都基于 Godot 的可禁用按钮，
            // 但公开的具体基类会随 UI 版本变化。通过实际节点上的属性设置，
            // 找不到属性时仍由下面的点击入口补丁兜底阻止放弃。
            AccessTools.Property(control.GetType(), "Disabled")?.SetValue(control, true);
            control.TooltipText = "怠惰结局中无法放弃";
        }
        catch (Exception exception)
        {
            ModLog.Write($"Could not visually disable the Sloth-route give-up button: {exception.Message}");
        }
    }

    public static void DisablePauseGiveUpButton(NPauseMenu pauseMenu)
    {
        if (!IsActive)
            return;

        var button = AccessTools.Field(typeof(NPauseMenu), "_giveUpButton")?.GetValue(pauseMenu) as Control;
        DisableControl(button);
    }

}

// 怠惰线不对怪物逐个造成伤害：实验体与多命小怪会把这种“击杀”视为一次
// 复活，仍要拖很多回合。改为直接复用原版的预完成结算流程，完全跳过怪物、
// 回合与战斗 UI，立即发放该房间本应有的奖励。建筑师始终放行原版流程。
[HarmonyPatch(typeof(EventOption), nameof(EventOption.ThatDoesDamage))]
internal static class SlothEventDamageOptionPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref decimal damage)
    {
        if (SlothRouteRules.ShouldSuppressEventHealthLoss)
            damage = 0m;
    }
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.ThatDecreasesMaxHp))]
internal static class SlothEventMaxHpOptionPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref decimal value)
    {
        if (SlothRouteRules.ShouldSuppressEventHealthLoss)
            value = 0m;
    }
}

// 有些事件会自行调用底层扣血命令，而不是使用标准 EventOption 辅助器。
// 仅在事件房中把实际失血归零，不会影响回归、火堆、战斗或其他正常生命变更。
[HarmonyPatch(typeof(Creature), "LoseHpInternal")]
internal static class SlothEventHealthExecutionPatch
{
    [HarmonyPrefix]
    private static void Prefix(Creature __instance, ref decimal amount)
    {
        if (SlothRouteRules.IsInEventRoom && __instance.IsPlayer)
            amount = 0m;
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.LoseMaxHp),
    new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(bool) })]
internal static class SlothEventMaxHpExecutionPatch
{
    [HarmonyPrefix]
    private static void Prefix(Creature creature, ref decimal amount)
    {
        if (SlothRouteRules.IsInEventRoom && creature.IsPlayer)
            amount = 0m;
    }
}

// 条件性必死事件也不再触发“爱你自己”的预警演出；标准伤害已经归零，
// 这里再清掉 WillKillPlayer 标记，保证 UI 与实际结果一致。
[HarmonyPatch(typeof(EventOption), nameof(EventOption.ThatWillKillPlayerIf))]
internal static class SlothEventLethalFlagPatch
{
    [HarmonyPostfix]
    private static void Postfix(EventOption __result)
    {
        if (SlothRouteRules.ShouldSuppressEventHealthLoss)
        {
            AccessTools.Property(typeof(EventOption), nameof(EventOption.WillKillPlayer))?.SetValue(
                __result,
                new Func<Player, bool>(_ => false));
        }
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook), nameof(MegaCrit.Sts2.Core.Hooks.Hook.ShouldAllowFreeTravel))]
internal static class SlothFreeTravelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (!RouteState.IsSlothRoute)
            return true;

        __result = true;
        return false;
    }
}

// ShouldAllowFreeTravel 只会放宽“下一步”的路线限制。怠惰线还要改写原版
// MapTravel 返回的可选节点，才能从当前层底直接选择 Boss 或任意其他地图点。
[HarmonyPatch(typeof(MapTravel), nameof(MapTravel.GetTravelablePointsFrom))]
internal static class SlothFullMapTravelPatch
{
    [HarmonyPostfix]
    private static void Postfix(IRunState runState, MapPoint currentPoint,
        ref IEnumerable<MapPoint> __result)
    {
        if (!RouteState.IsSlothRoute || runState.Map is null)
            return;

        // 这里只返回常规网格点。Boss 点虽然也需要支持从任意楼层直达，
        // 但它们不在 NMapScreen 的 _mapPointDictionary 中；如果把 Boss 点
        // 塞进这个结果，原版 RecalculateTravelability 会用字典索引它们，
        // 在高层火堆重新打开地图时就会中断整次旅行状态计算。
        var points = runState.Map.GetAllMapPoints()
            .Where(point => !ReferenceEquals(point, currentPoint))
            .ToHashSet();

        __result = points;
    }
}

// Boss 与第二 Boss 是地图屏幕中的独立节点，不能通过上面的普通点结果
// 参与原版字典遍历；单独把它们标为可旅行即可保留“从任意楼层直达 Boss”。
// 另外原版 RecalculateTravelability 有个短路：当前位于最后一行时直接只标
// Boss 并 return，不调用 GetTravelablePointsFrom（全图补丁因此失效，表现
// 为最后一层火堆之后只能打 Boss）。这里在怠惰线下把全部普通点重新标为
// 可旅行，恢复全图旅行。
[HarmonyPatch(typeof(NMapScreen), "RecalculateTravelability")]
internal static class SlothBossTravelabilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        if (!RouteState.IsSlothRoute || !GodotObject.IsInstanceValid(__instance))
            return;

        if (AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary")?.GetValue(__instance)
            is Dictionary<MapCoord, NMapPoint> mapPoints)
        {
            foreach (var node in mapPoints.Values)
            {
                if (GodotObject.IsInstanceValid(node) && node.State == MapPointState.Untravelable)
                    node.State = MapPointState.Travelable;
            }
        }

        SetSpecialPointTravelable(__instance, "_bossPointNode");
        SetSpecialPointTravelable(__instance, "_secondBossPointNode");
    }

    private static void SetSpecialPointTravelable(NMapScreen screen, string fieldName)
    {
        if (AccessTools.Field(typeof(NMapScreen), fieldName)?.GetValue(screen) is NMapPoint node &&
            GodotObject.IsInstanceValid(node))
        {
            node.State = MapPointState.Travelable;
        }
    }
}

// RestSiteRoom 在显示“前进”按钮时会调用原版 SetTravelEnabled(true)。
// 高层火堆处原版的 ShouldProceedToNextMapPoint 可能仍返回 false，导致
// IsTravelEnabled 被原版覆盖为 false；怠惰线应在此处恢复地图行动权限。
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled), new[] { typeof(bool) })]
internal static class SlothMapTravelEnabledPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance, bool __0)
    {
        if (!RouteState.IsSlothRoute || !__0 || !GodotObject.IsInstanceValid(__instance))
            return;

        if (!__instance.IsTravelEnabled)
        {
            AccessTools.Field(typeof(NMapScreen), "<IsTravelEnabled>k__BackingField")?
                .SetValue(__instance, true);
            ModLog.Write("Sloth route restored map travel after the native rest-site proceed flow.");
        }

        try
        {
            AccessTools.Method(typeof(NMapScreen), "RecalculateTravelability")?.Invoke(__instance, null);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Sloth map travelability refresh failed: {exception.Message}");
        }
    }
}

// 死亡前的探索记忆：真正死亡（或放弃走向回归）时，把当前时间线的地图探索
// 情况按层记录到 mod 文件。节点分两类——
//   已探索（Explored）：本条时间线走过的全部房间（原版 VisitedMapCoords）；
//   探索中（Exploring）：时间线结束时所在的房间，即战斗/事件死亡或放弃的位置。
// 原版只在房间完成后才计入 VisitedMapCoords，死亡位置必须单独记录，否则
// 回归后地图只显示到死亡节点的父节点、连向死亡节点的路线会缺失。
// 路线（Routes）在玩家选路时记录：EnterMapCoord 触发、刚完成的节点还在
// CurrentMapCoord 上，此时该节点展示过的全部分支（含未选择的）都是玩家
// 亲眼见过的信息，按记录显示即可，无需推断行进方向；死亡节点从未完成过，
// 它的分支不会被记录，因此不会提前泄露。
// 同一局内多次死亡按层合并累计，同一条边去重，新开局时清空。
[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
internal static class SlothPauseGiveUpVisualPatch
{
    [HarmonyPostfix]
    private static void Postfix(NPauseMenu __instance) =>
        SlothRouteRules.DisablePauseGiveUpButton(__instance);
}

// 设置页的放弃按钮可能在进入怠惰线前就已创建，因此同时覆盖创建和获得焦点两刻。
[HarmonyPatch(typeof(NAbandonRunButton), "_Ready")]
internal static class SlothSettingsGiveUpReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NAbandonRunButton __instance) =>
        SlothRouteRules.DisableControl(__instance);
}

[HarmonyPatch(typeof(NAbandonRunButton), "OnFocus")]
internal static class SlothSettingsGiveUpFocusPatch
{
    [HarmonyPostfix]
    private static void Postfix(NAbandonRunButton __instance) =>
        SlothRouteRules.DisableControl(__instance);
}

// 玩家前进（通过 EnterMapCoord 进入新地图点）即清除火堆落点标记与未决分支，
// “放弃”恢复原版行为。不用 AppendToMapPointHistory 判断：回归落地时原版
// LoadRun 也会为当前房间追加历史条目，那并不是玩家前进。
