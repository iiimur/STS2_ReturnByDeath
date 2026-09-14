// 按功能拆分；仍编译进同一个 ReturnByDeath DLL。

namespace ReturnByDeath;

// 蕾姆事件：死亡回归回到“休息过的火堆”检查点（当前地图点历史带 HEAL）、
// 牌组含至少 2 张愧疚、且不在任何 IF 线上时，点击暂停菜单/设置屏幕的
// “放弃”（按钮外观不变）即触发事件，菜单自动关闭。
// 弹出原版“确认放弃”同款弹窗（复用 abandon_run_confirm_popup 场景）：
// 每个弹窗正文上方有一行蓝色引言（带引号），弹出同时播放对应音频——
//   1)“放弃是简单的，但是，不适合你。”　无标题正文，引言居中；右侧按钮
//     变为“……”，其上方右对齐小字“为什么即使我这样你还……”；
//   2)“因为，你是我的英雄啊！”　　　　　同上单按钮，
//     小字“在意我这样的人，真的没问题吗”；
//   3)“从这里开始吧，从一开始，不……从零开始！”（同前两个弹窗的格式）。
// 三个弹窗连续弹出，效果集中在最后统一施加；期间 SL 重载后点“放弃”
// 会回到当前弹窗（音频随弹窗重新播放）。
// 第四个弹窗保留原生标题与正文（无蓝色引言），左右对调成
// “左绿不了 / 右红好的”决定分支：
//   绿色“不了”→ 回满血（上限补到 86、86/86）与删除所有愧疚一起生效，
//   400ms 后播放蕾姆的从零开始（50% 音量），再 1.5s 后从角色全卡池的升级
//   稀有卡中任选 1 张加入牌组；其数字费用永久固定为 0、X 费保持原样，
//   日后被移除时进入愤怒 IF 线；
//   红色“好的”→ 进入怠惰 IF 线（掐断自杀流程、继续正常游戏）。
internal static class RemEventState
{
    private sealed class RemFile
    {
        // 0 = 无未决分支；1..3 = 该分支弹窗待重入。
        public int PendingStage { get; set; }
        public bool Used { get; set; }
        // 最终选择绿色“不了”后置位；第一层 Boss 胜利时据此追加遗忘之魂。
        public bool ChoseRewardBranch { get; set; }
        public bool ForgottenSoulGranted { get; set; }
        // “不了”奖励牌的持久身份。ModelId 拆成两个字符串，避免依赖游戏
        // record 类型的 System.Text.Json 构造规则；同名牌用获得楼层与序号区分。
        public bool RewardCardSelected { get; set; }
        public string? RewardCardCategory { get; set; }
        public string? RewardCardEntry { get; set; }
        public int RewardCardUpgradeLevel { get; set; }
        public int? RewardCardFloorAddedToDeck { get; set; }
        public int RewardCardOrdinal { get; set; }
        public ulong RewardCardPlayerNetId { get; set; }
        public bool RewardCardRemoved { get; set; }
    }

    private const string RewardSfxFile = "蕾姆的从零开始.wav";
    // 三个分支弹窗各自同时播放的音频与正文上方的蓝色引言（带引号）。
    private static readonly string[] BranchAudio =
    {
        "放弃是简单的.wav",
        "蕾姆的英雄.wav",
        "从零开始.wav"
    };
    private static readonly string[] BranchQuotes =
    {
        "放弃是简单的，但是，不适合你。",
        "因为，你是我的英雄啊！",
        "从这里开始吧，从一开始，不……从零开始！"
    };
    private const string QuoteColor = "89c4e1";
    // 前两个弹窗最下方的小字（无引号）；第三段没有小字。
    private static readonly string[] BranchFooters =
    {
        "为什么即使我这样你还",
        "在意我这样的人，真的没问题吗",
        ""
    };
    private static readonly string StatePath = Path.Combine(ModLog.ModDirectory, "return-by-death.rem-state.json");

    private static readonly object Sync = new();
    private static RemFile? _file;
    private static int _flowRunning;
    private static int _forgottenSoulGrantInProgress;
    private static WeakReference<CardModel>? _rewardCard;
    private static readonly HashSet<CardModel> RewardPreviewCards = new(
        (IEqualityComparer<CardModel>)ReferenceEqualityComparer.Instance);

    public static void ResetForNewRun()
    {
        lock (Sync)
        {
            _file = new RemFile();
            try { File.Delete(StatePath); } catch { }
        }
        Volatile.Write(ref _forgottenSoulGrantInProgress, 0);
        _rewardCard = null;
        lock (RewardPreviewCards)
            RewardPreviewCards.Clear();
    }

    public static void OnMapAdvanced()
    {
        try
        {
            // 前进到新地图点：离开火堆存档，“火堆落点”标记与未决分支一并清除。
            RouteState.SetAtRestedFireCheckpoint(false);
            lock (Sync)
            {
                var file = EnsureLoaded();
                if (file.PendingStage == 0)
                    return;
                file.PendingStage = 0;
                Save();
                ModLog.Write("Rem event pending branch cleared: the player moved forward.");
            }
        }
        catch (Exception exception)
        {
            // 该回调跑在原版进入地图点的调用链里，异常会炸掉原生流程，必须吞掉。
            ModLog.Write($"Rem map-advance handling failed: {exception}");
        }
    }

    // 保存退出/回到主菜单时清理进程内状态；未决分支保留在文件里，
    // 读档后仍在火堆存档处可继续触发。
    // 注意：不要在蕾姆流程中触碰 NRunMusicController（StopMusic/UpdateMusic/
    // UpdateMusic 抑制都试过，均会在进入下一个休息处时挂死原生音乐系统）。
    public static void OnLeftRun()
    {
        RestoreBgmVolume();
        _popupCompletion = null;
        _popupInverted = false;
        _rewardCard = null;
        ClearRewardCandidates();
        Volatile.Write(ref _flowRunning, 0);
    }

    // 暂停菜单/设置屏幕的“放弃”按下入口。返回 true 表示蕾姆事件已接管本次点击。
    public static bool TryHandleGiveUpPressed()
    {
        if (Volatile.Read(ref _flowRunning) != 0)
            return true;

        var stage = GetPendingStage();
        if (stage > 0)
        {
            // 未决分支重入：直接回到当前确认窗（或选卡页）。
            Volatile.Write(ref _flowRunning, 1);
            ClosePauseMenu();
            _ = ResumePendingAsync(stage);
            return true;
        }

        if (!CanOfferEvent())
            return false;

        Volatile.Write(ref _flowRunning, 1);
        ClosePauseMenu();
        _ = StartFlowAsync();
        return true;
    }

    // 事件接管后立即关闭暂停菜单（顶栏暂停图标同步复位），避免菜单遮挡
    // 后面的视频与弹窗；确认弹窗走独立的 NModalContainer，不受影响。
    private static void ClosePauseMenu()
    {
        try
        {
            MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance?.Close();
            if (NRun.Instance?.GlobalUi is { } globalUi &&
                FindDescendant<MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton>(globalUi) is { } pauseButton)
            {
                pauseButton.ToggleAnimState();
            }
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem close pause menu failed: {exception.Message}");
        }
    }

    private static T? FindDescendant<T>(Node root) where T : Node
    {
        if (root is T match)
            return match;
        foreach (var child in root.GetChildren())
        {
            var found = FindDescendant<T>(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static int GetPendingStage()
    {
        lock (Sync)
            return EnsureLoaded().PendingStage;
    }

    private static void SetPendingStage(int stage)
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.PendingStage = stage;
            Save();
        }
    }

    private static void MarkUsed()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.Used = true;
            file.PendingStage = 0;
            Save();
        }
    }

    private static void MarkRewardBranchChosen()
    {
        IfAchievements.Unlock("rem_reward");
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.ChoseRewardBranch = true;
            Save();
        }
    }

    // 选卡页里的候选牌也要立即显示为 0 费（X 费保持原样）；这些临时实例只按引用记录，
    // 关闭选卡页后清空，不会把同名的普通卡误判成蕾姆奖励。
    private static void PrepareRewardCandidate(CardModel card)
    {
        SetRewardCost(card);
        lock (RewardPreviewCards)
            RewardPreviewCards.Add(card);
    }

    private static void ClearRewardCandidates()
    {
        lock (RewardPreviewCards)
            RewardPreviewCards.Clear();
    }

    private static void SetRewardCost(CardModel card)
    {
        if (!card.EnergyCost.CostsX)
            card.EnergyCost.SetCustomBaseCost(0);
    }

    private static bool MatchesRewardSignature(CardModel card, RemFile file)
    {
        if (!file.RewardCardSelected || file.RewardCardRemoved ||
            card.Id.Category != file.RewardCardCategory ||
            card.Id.Entry != file.RewardCardEntry ||
            card.CurrentUpgradeLevel != file.RewardCardUpgradeLevel ||
            card.FloorAddedToDeck != file.RewardCardFloorAddedToDeck)
        {
            return false;
        }

        return card.Owner?.NetId == file.RewardCardPlayerNetId;
    }

    // 在奖励真正加入 Deck 后记录身份。费用规则本身由运行时补丁保证，
    // 因而保存退出和死亡回归重新构造卡牌后仍然是 0 费。
    private static void MarkRewardCard(CardModel card, Player owner)
    {
        SetRewardCost(card);
        lock (Sync)
        {
            var file = EnsureLoaded();
            file.RewardCardSelected = true;
            file.RewardCardCategory = card.Id.Category;
            file.RewardCardEntry = card.Id.Entry;
            file.RewardCardUpgradeLevel = card.CurrentUpgradeLevel;
            file.RewardCardFloorAddedToDeck = card.FloorAddedToDeck;
            file.RewardCardPlayerNetId = owner.NetId;
            file.RewardCardRemoved = false;

            var matches = owner.Deck.Cards.Where(candidate =>
                candidate.Id == card.Id &&
                candidate.CurrentUpgradeLevel == card.CurrentUpgradeLevel &&
                candidate.FloorAddedToDeck == card.FloorAddedToDeck).ToList();
            var ordinal = matches.FindIndex(candidate => ReferenceEquals(candidate, card));
            file.RewardCardOrdinal = Math.Max(0, ordinal);
            _rewardCard = new WeakReference<CardModel>(card);
            Save();
        }
        ModLog.Write($"Rem reward card marked as permanently free: {card.Id} (floor={card.FloorAddedToDeck}, ordinal={GetRewardCardOrdinal()}).");
    }

    private static int GetRewardCardOrdinal()
    {
        lock (Sync)
            return EnsureLoaded().RewardCardOrdinal;
    }

    // 费用查询既可能收到永久牌组实例，也可能收到它的战斗副本；战斗副本
    // 通过 DeckVersion 回指原牌。首次读档后则按持久签名重新绑定引用。
    internal static bool IsRewardCostCard(CardModel? card)
    {
        if (card is null)
            return false;

        lock (RewardPreviewCards)
        {
            if (RewardPreviewCards.Contains(card))
                return true;
        }

        var deckCard = card.Pile?.Type == PileType.Deck ? card : card.DeckVersion;
        if (deckCard is null)
            return false;

        lock (Sync)
        {
            var file = EnsureLoaded();
            if (!file.RewardCardSelected || file.RewardCardRemoved)
                return false;

            if (_rewardCard is not null && _rewardCard.TryGetTarget(out var bound))
            {
                if (ReferenceEquals(deckCard, bound))
                    return true;

                // 同一副当前牌组里的同名牌不是奖励牌；死亡回归或读档重建了
                // Player/Deck 时旧引用才算失效，随后按签名绑定到新实例。
                if (ReferenceEquals(bound.Owner, deckCard.Owner) &&
                    bound.Owner?.Deck.Cards.Any(candidate => ReferenceEquals(candidate, bound)) == true)
                {
                    return false;
                }

                _rewardCard = null;
            }

            var owner = deckCard.Owner;
            if (owner is null || owner.NetId != file.RewardCardPlayerNetId)
                return false;

            var matches = owner.Deck.Cards.Where(candidate => MatchesRewardSignature(candidate, file)).ToList();
            if (matches.Count == 0)
                return false;

            // 若奖励牌之前存在同层获得的同名牌，而那张普通牌后来先被删除，
            // 持久序号会向前收缩；取当前可用的最后序号即可继续锁定原奖励牌。
            var resolvedIndex = Math.Clamp(file.RewardCardOrdinal, 0, matches.Count - 1);
            if (resolvedIndex != file.RewardCardOrdinal)
            {
                file.RewardCardOrdinal = resolvedIndex;
                Save();
            }
            var resolved = matches[resolvedIndex];
            _rewardCard = new WeakReference<CardModel>(resolved);
            SetRewardCost(resolved);
            return ReferenceEquals(deckCard, resolved);
        }
    }

    internal static bool IsRewardCardBeingRemoved(CardModel? card) =>
        card is not null && card.Pile?.Type == PileType.Deck && IsRewardCostCard(card);

    // 原生删牌任务成功结束后只消费一次奖励牌身份，再进入愤怒线。
    internal static bool CompleteRewardCardRemoval()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (!file.RewardCardSelected || file.RewardCardRemoved)
                return false;

            file.RewardCardRemoved = true;
            _rewardCard = null;
            Save();
            return true;
        }
    }

    // 只在第一层 Boss 奖励结算时由外部补丁调用。进程内原子标记防止多个
    // OfferRoomEndRewards 后缀并行重复发放；成功后再写入本局持久化状态。
    public static bool TryBeginForgottenSoulGrant()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (!file.ChoseRewardBranch || file.ForgottenSoulGranted)
                return false;
        }

        return Interlocked.CompareExchange(ref _forgottenSoulGrantInProgress, 1, 0) == 0;
    }

    public static void FinishForgottenSoulGrant(bool granted)
    {
        try
        {
            if (granted)
            {
                lock (Sync)
                {
                    var file = EnsureLoaded();
                    file.ForgottenSoulGranted = true;
                    Save();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _forgottenSoulGrantInProgress, 0);
        }
    }

    private static RemFile EnsureLoaded()
    {
        if (_file is not null)
            return _file;

        try
        {
            _file = JsonSerializer.Deserialize<RemFile>(File.ReadAllText(StatePath)) ?? new RemFile();
        }
        catch
        {
            _file = new RemFile();
        }

        return _file;
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_file));
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem event state save failed: {exception.Message}");
        }
    }

    private static bool CanOfferEvent()
    {
        lock (Sync)
        {
            var file = EnsureLoaded();
            if (file.Used || RouteState.IsIfRoute || RouteState.IsSlothRoute)
                return false;
        }

        if (!RouteState.IsRecoveredFromDeath)
            return false;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var player = state?.Players.FirstOrDefault();
        if (player is null)
            return false;

        // 回归落点必须是休息过的火堆检查点（恢复落地时写入的标记）。
        if (!RouteState.IsAtRestedFireCheckpoint)
            return false;

        if (player.Deck.Cards.Count(card => card is Guilty) < 2)
            return false;

        return true;
    }

    private static async Task StartFlowAsync()
    {
        ModLog.Write("Rem event started.");
        await AdvanceAsync(1);
    }

    // SL 重载后的重入：回到当前分支的弹窗（音频随弹窗重新播放），效果尚未
    // 施加（效果在“好的”之后），因此走与正常推进完全相同的路径。
    private static async Task ResumePendingAsync(int stage)
    {
        ModLog.Write($"Rem event resumed at pending stage {stage}.");
        if (stage >= 5)
        {
            // SL 前已选“不了”、正在选牌：直接回到选卡页面。
            SetPendingStage(0);
            await ShowRewardSelectionAsync();
            MarkUsed();
            FinishFlow("reward granted after reload.");
            return;
        }

        await AdvanceAsync(stage);
    }


    // 事件期间的 BGM 闪避：走设置菜单同款运行时音量接口（NAudioManager），
    // 只改音量、不动 FMOD 事件与音轨银行，不会触发休息处的原生挂死。
    private static float? _savedBgmVolume;

    private static void DuckBgmVolume()
    {
        try
        {
            var save = SaveManager.Instance?.SettingsSave;
            if (save is null || _savedBgmVolume.HasValue)
                return;

            _savedBgmVolume = save.VolumeBgm;
            NAudioManager.Instance?.SetBgmVol(_savedBgmVolume.Value * 0.15f);
            ModLog.Write($"Rem event BGM ducked to 15% of {_savedBgmVolume.Value:0.00}.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem BGM duck failed: {exception.Message}");
        }
    }

    private static void RestoreBgmVolume()
    {
        try
        {
            if (_savedBgmVolume is not { } volume)
                return;

            _savedBgmVolume = null;
            NAudioManager.Instance?.SetBgmVol(volume);
            ModLog.Write("Rem event BGM volume restored.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem BGM restore failed: {exception.Message}");
        }
    }

    private static void FinishFlow(string reason)
    {
        RestoreBgmVolume();
        Volatile.Write(ref _flowRunning, 0);
        ModLog.Write($"Rem event finished: {reason}");
    }


    // 分支推进：先（可选）播视频，播完立即撤掉视频层（NModalContainer 的弹窗
    // 在默认画布，层级低于视频层，不撤会被黑屏盖住），弹确认窗的同时施加效果；
    // 选“不了”挂起，选“好的”进入下一段视频；第三段“好的”进入怠惰线、“不了”进入奖励。
    // 分支推进：弹确认窗（同时播放对应音频与蓝色引言）；“好的”后施加该
    // 分支效果并推进到下一分支。pending 在弹窗出现前就持久化：选择做出前
    // 任何 SL 重载，点“放弃”都会回到当前弹窗（音频随弹窗重新播放）。
    // 分支推进：三个确认窗连续弹出（同时播放对应音频与蓝色引言），分支
    // 效果不在单个弹窗后施加。pending 在弹窗出现前就持久化：选择做出前
    // 任何 SL 重载，点“放弃”都会回到当前弹窗（音频随弹窗重新播放）。
    // 分支推进：三个确认窗连续弹出（同时播放对应音频与蓝色引言），效果
    // 不在单个弹窗后施加。pending 在弹窗出现前就持久化：选择做出前任何
    // SL 重载，点“放弃”都会回到当前弹窗（音频随弹窗重新播放）。
    private static async Task AdvanceAsync(int stage)
    {
        try
        {
            if (stage <= 3)
            {
                // BGM 随台词压低，台词播完即恢复（Finished 回调触发）。
                DuckBgmVolume();
                CosmeticAudio.TryPlay(BranchAudio[stage - 1], onFinished: RestoreBgmVolume);
            }
            SetPendingStage(stage);
            var accepted = await ShowConfirmPopupAsync(stage);
            // 点击回调和原生 Clear 在同一帧内先后执行：Clear 会对容器内所有
            // 子节点 QueueFree。这里等旧弹窗真正释放后再创建下一个弹窗，
            // 否则新弹窗会被随后的 Clear 一起删掉（表现为“点掉不出下一个”）。
            await WaitSeconds(0.1);

            if (stage == 4)
            {
                // 第四个弹窗：原生格式的分支选择（左绿“不了” / 右红“好的”）。
                if (!accepted)
                {
                    // 绿色“不了”：回血与删愧疚一起生效，400ms 后播放蕾姆的
                    // 从零开始（50% 音量），再 1s 后进入选卡。pending 阶段 5
                    // 使 SL 后直接回到选牌（效果此时已生效，不会重复施加）。
                    MarkRewardBranchChosen();
                    await ApplyFinaleEffectsAsync();
                    SetPendingStage(5);
                    await WaitSeconds(0.4);
                    DuckBgmVolume();
                    CosmeticAudio.TryPlay(RewardSfxFile, 0.5f, onFinished: RestoreBgmVolume);
                    await WaitSeconds(1.5);
                    await ShowRewardSelectionAsync();
                    MarkUsed();
                    FinishFlow("reward granted after the final branch.");
                    return;
                }

                // 红色“好的”：进入「怠惰」IF 线，掐断自杀流程。
                // 与正常结局触发完全相同的代码（线路标记 + 怠惰之心 + 结局演出）。
                await EnterSlothAsync(playPresentation: true);
                MarkUsed();
                FinishFlow("sloth IF route entered.");
                return;
            }

            await AdvanceAsync(stage + 1);
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem event flow failed at branch {stage}: {exception}");
            FinishFlow("flow error.");
        }
    }

    // 进入怠惰线的完整流程：线路标记 + 授予怠惰之心。结局演出（音效 + 羽化
    // 结局图）由 playPresentation 控制；控制台 boss sloth 以 false 复用同一套
    // 代码，因此同样能拿到怠惰之心。
    internal static async Task EnterSlothAsync(bool playPresentation)
    {
        RouteState.EnterSlothRoute();
        await GrantHeartOfSlothAsync();
        if (playPresentation)
            SlothEndingOverlay.TryShow();
    }

    // 选择“好的”进入怠惰线时的遗物授予：怠惰之心本身就是“进入怠惰线”的
    // 载体，拿到手即写入检查点，死亡回归不消失。
    private static async Task GrantHeartOfSlothAsync()
    {
        try
        {
            var player = RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault();
            if (player is null || player.Relics.Any(relic => relic is HeartOfSloth))
                return;

            var relic = await RelicCmd.Obtain<HeartOfSloth>(player);
            CheckpointStore.RecordEchidnaRelic(relic, player);
            ModLog.Write("Sloth IF route entered; Heart of Sloth granted and persisted.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Heart of Sloth grant failed: {exception}");
        }
    }

    private static async Task WaitSeconds(double seconds)
    {        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree is null)
            return;
        // ToSignal 在主循环恢复，保证后续节点操作仍在主线程执行。
        await tree.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    // 终幕效果：回满血（上限不足 86 补到 86，86/86）与删除牌组中所有愧疚
    // 同时进行；删牌动画与回血演出并行播放。
    private static async Task ApplyFinaleEffectsAsync()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var player = state?.Players.FirstOrDefault();
            if (player is null)
                return;

            var removeTask = RemoveGuiltiesAsync(player);

            if (player.Creature.MaxHp < CheckpointStore.InitialMaxHealth)
                player.Creature.SetMaxHpInternal(CheckpointStore.InitialMaxHealth);
            var missing = player.Creature.MaxHp - player.Creature.CurrentHp;
            if (missing > 0m)
                await CreatureCmd.Heal(player.Creature, missing, true);
            // CreatureCmd.Heal 自带战斗回血音效（event:/sfx/heal），无需额外播放。
            ModLog.Write($"Rem finale: healed to {player.Creature.CurrentHp}/{player.Creature.MaxHp}.");

            await removeTask;
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem finale effects failed: {exception}");
        }
    }

    private static async Task RemoveGuiltiesAsync(Player player)
    {
        try
        {
            var guilties = player.Deck.Cards.Where(card => card is Guilty).ToArray();
            if (guilties.Length > 0)
                await CardPileCmd.RemoveFromDeck(guilties, true);
            ModLog.Write($"Rem finale: removed {guilties.Length} guilties.");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem finale guilt removal failed: {exception}");
        }
    }

    // 原版“确认是否放弃”同款弹窗：直接复用 abandon_run_confirm_popup 场景
    // （NModalContainer 要求节点实现 IScreenContext，裸的 vertical_popup 放不进去），
    // 其 _Ready 会用原版文案初始化两个按钮；原版 OnYes/OnNo（放弃/关闭）由
    // RemPopupYes/NoPatch 拦截，点击结果经 TryConsumePopupChoice 交回流程，
    // 弹窗本身由按钮内置的 Close（NModalContainer.Clear）关闭。第三段红绿互换。
    private static TaskCompletionSource<bool>? _popupCompletion;

    private static async Task<bool> ShowConfirmPopupAsync(int stage)
    {
        var completion = new TaskCompletionSource<bool>();
        _popupCompletion = completion;
        _popupInverted = false;
        var popup = NAbandonRunConfirmPopup.Create(null)
            ?? throw new InvalidOperationException("Abandon confirm popup is unavailable (test mode?).");
        NModalContainer.Instance!.Add(popup);

        // 等原版 _Ready 完成按钮初始化（最多等 1.2 秒），然后：
        //   在正文“放弃游戏会视为本局失败。”上方加一行蓝色引言（带引号）；
        //   第三段再把左右按钮对调成“左绿不了 / 右红好的”。
        for (var i = 0; i < 60; i++)
        {
            if (AccessTools.Field(typeof(NPopupYesNoButton), "_label")?
                .GetValue(popup.GetNode<NVerticalPopup>("VerticalPopup").YesButton) is not null)
                break;
            await WaitSeconds(0.02);
        }

        var inner = popup.GetNode<NVerticalPopup>("VerticalPopup");
        var bodyLabel = popup.GetNode<MegaRichTextLabel>("VerticalPopup/Description");
        bodyLabel.BbcodeEnabled = true;

        if (stage <= 3)
        {
            // 前三个弹窗：去掉标题与原生正文，只保留居中的蓝色引言；
            // 删除左侧“不了”，右侧按钮改为“……”；自白小字放在按钮左侧
            // （右缘对齐按钮，比按钮宽，右对齐收尾）。
            inner.SetText("", "");
            var quote = $"[color=#{QuoteColor}]“{BranchQuotes[stage - 1]}”[/color]";
            bodyLabel.SetTextAutoSize($"[center]{quote}[/center]");
            inner.HideNoButton();
            inner.NoButton.DisconnectHotkeys();
            inner.YesButton.SetText(stage == 3 ? "做出选择" : "……");
            AddPopupFooter(inner, BranchFooters[stage - 1]);
        }
        else
        {
            // 第四个弹窗：保留原生标题与正文（无蓝色引言），左右按钮对调成
            // “左绿不了 / 右红好的”，由它决定分支走向。
            SwapPopupButtonSides(popup);
            _popupInverted = true;
        }
        return await completion.Task;
    }

    // 返回 true 表示该次点击属于蕾姆流程，结果已交回流程并应拦截原版行为。
    // 左右对调后的弹窗里 YesButton 节点显示“不了”，回调结果需要取反。
    private static bool _popupInverted;

    public static bool TryConsumePopupChoice(bool yes)
    {
        var completion = _popupCompletion;
        if (completion is null)
            return false;

        _popupCompletion = null;
        var result = _popupInverted ? !yes : yes;
        _popupInverted = false;
        completion.TrySetResult(result);
        return true;
    }


    // 最终弹窗左右对调：原版是“左红不了 / 右绿好的”。对调两个按钮节点的
    // 位置与文案后即变成“左绿不了 / 右红好的”，颜色全部来自原版美术。
    // YesButton 节点（绿色）挪到左侧显示“不了”，NoButton（红色）挪到右侧显示
    // “好的”；点击回调的结果由 _popupInverted 取反，热键也随 IsYes 对调。
    private static void SwapPopupButtonSides(NAbandonRunConfirmPopup popup)
    {
        try
        {
            var inner = popup.GetNode<NVerticalPopup>("VerticalPopup");
            var yes = inner.YesButton;
            var no = inner.NoButton;
            var yesPosition = yes.Position;
            yes.Position = no.Position;
            no.Position = yesPosition;
            yes.SetText(new LocString("main_menu_ui", "GENERIC_POPUP.cancel").GetFormattedText());
            no.SetText(new LocString("main_menu_ui", "GENERIC_POPUP.confirm").GetFormattedText());
            yes.IsYes = false;
            no.IsYes = true;
            ModLog.Write("Rem final popup sides swapped (left green 不了 / right red 好的).");
        }
        catch (Exception exception)
        {
            ModLog.Write($"Rem popup side swap failed: {exception}");
        }
    }

    // 自白小字：放在绿色“……”按钮的左侧同一行，右缘贴住按钮左缘——
    // 字号与按钮省略号一致，自白的句尾与按钮的“……”连成一句话。
    // 灰色小字，不响应鼠标。
    private static void AddPopupFooter(NVerticalPopup inner, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var button = inner.YesButton;
        var buttonLabel = button.GetNodeOrNull<MegaLabel>("Label");
        var fontSize = buttonLabel?.GetThemeFontSize("font_size") ?? 20;
        var footer = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = $"[right]{text}[/right]",
            ScrollActive = false,
            FitContent = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        footer.AddThemeFontSizeOverride("normal_font_size", fontSize);
        footer.AddThemeColorOverride("default_color", new Color(0.78f, 0.78f, 0.78f));
        inner.AddChild(footer);
        const float width = 520f;
        footer.Position = new Vector2(button.Position.X - width, button.Position.Y + (button.Size.Y - 44f) / 2f);
        footer.Size = new Vector2(width, 44f);
    }


    // 复制卡奖励：选卡池为“该角色全卡池中的稀有卡”，全部以升级形态和
    // 数字费用固定为 0（X 费保持原样），只选 1 张加入牌组。选卡 UI 走原版奖励网格
    // （与“奶酪房间”事件同一条通道），未选中的实例随后废弃。
    private static async Task ShowRewardSelectionAsync()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        var player = state?.Players.FirstOrDefault();
        if (player is null)
            return;

        var rareOptions = CardCreationOptions.ForNonCombatWithUniformOdds(
            new[] { player.Character.CardPool },
            card => card.Rarity == CardRarity.Rare);
        var possible = rareOptions.GetPossibleCards(player).ToList();
        if (possible.Count == 0)
        {
            // 角色卡池必有稀有卡；真为空时放宽到全部解锁卡。
            possible = CardCreationOptions
                .ForNonCombatWithUniformOdds(new[] { player.Character.CardPool })
                .GetPossibleCards(player)
                .ToList();
        }

        if (possible.Count == 0)
        {
            ModLog.Write("Rem reward skipped: the character card pool is empty.");
            return;
        }

        // 逐张实例化为归属玩家的升级形态；数字费用临时标为 0，X 费保持原样。
        var results = possible.Select(model =>
        {
            var card = player.RunState.CreateCard(model, player);
            card.UpgradeInternal();
            PrepareRewardCandidate(card);
            return new CardCreationResult(card);
        }).ToList();
        ModLog.Write($"Rem reward pool: {results.Count} upgraded rare cards; numeric costs are zero and X costs are unchanged.");

        IEnumerable<CardModel> selected;
        try
        {
            var prefs = new CardSelectorPrefs(new LocString("return-by-death", "rem-reward-prompt"), 1);
            selected = await CardSelectCmd.FromSimpleGridForRewards(
                new BlockingPlayerChoiceContext(), results, player, prefs);
        }
        finally
        {
            ClearRewardCandidates();
        }

        foreach (var card in selected)
        {
            var addResult = await CardPileCmd.Add(card, PileType.Deck);
            CardCmd.PreviewCardPileAdd(addResult);
            if (addResult.success && addResult.cardAdded is { } added)
            {
                MarkRewardCard(added, player);
                ModLog.Write($"Rem reward: added upgraded rare card {added.Id}; numeric cost is permanently zero and X cost is unchanged.");
            }
        }
    }

}
