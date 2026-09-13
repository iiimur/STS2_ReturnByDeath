// 调试命令：hp xx —— 把本地玩家的当前血量设为 xx（自动夹到 0..血量上限）。
// 设置后如果地图开着，立即刷新墓碑按钮的可见性，方便测试艾姬多娜的开放门控。

using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;

namespace ReturnByDeath;

public sealed class HpConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "hp";

    public override string Args => "<hp:int>";

    public override string Description => "Set your current HP (clamped to 0..MaxHp); refreshes tombstone visibility.";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer is null)
            return new CmdResult(success: false, "No player found.");

        if (args.Length != 1 || !int.TryParse(args[0], out var hp))
            return new CmdResult(success: false, "Usage: hp <number>");

        var creature = issuingPlayer.Creature;
        var clamped = Math.Clamp(hp, 0, creature.MaxHp);
        creature.SetCurrentHpInternal(clamped);

        var mapScreen = NMapScreen.Instance;
        if (mapScreen is not null && mapScreen.IsOpen)
            TombstoneEntry.Ensure(mapScreen);

        return new CmdResult(success: true, $"HP set to {clamped}/{creature.MaxHp}.");
    }
}
