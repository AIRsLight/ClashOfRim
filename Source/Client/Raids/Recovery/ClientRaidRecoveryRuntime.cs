using AIRsLight.ClashOfRim.Modules.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidRecoveryRuntime : IClientRaidRecoveryRuntime
{
    private readonly Game? game = Current.Game;
    public bool Ready => game is not null && ReferenceEquals(Current.Game, game) && game.World?.worldObjects is not null && game.Maps is not null;
    public IClientRaidRecoveredBattle? ReadBattle() => Ready && ClashOfRimGameComponent.ActiveRaidBattleSession is { } battle
        ? new ClientRaidSettlementRuntime(battle) : null;
}
