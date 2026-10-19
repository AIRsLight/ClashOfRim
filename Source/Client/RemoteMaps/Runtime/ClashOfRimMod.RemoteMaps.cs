using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.Modules.Raids;
using RimWorld;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenCaravanRaidMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets)
        => raidCommands.OpenCaravanMenu(ClientRaidCommandSource.ForCaravan(caravan), targets);

    public void StartRaidFromVehicleLanding(Caravan caravan, ModWorldMapMarkerDto target)
        => _ = raidCommands.StartVehicle(ClientRaidCommandSource.ForCaravan(caravan), target);

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
        => observationMaps.OpenAsync(target, mode);

    internal void AdvanceRaidRecovery() { snapshotReload.Advance(); raidRecovery.Advance(); }
    internal bool RaidRecoveryBlocksBattleLifecycle => raidRecovery.BlocksBattleLifecycle || snapshotReload.BlocksGameLifecycle;

    internal void StartFinishActiveRaidBattle(ActiveRaidBattleSession session, string finishReason)
        => _ = raidSettlements.StartAsync(new ClientRaidSettlementRuntime(session), finishReason);

    internal void HandleRaidBattleFinalDeadlineExpired(ActiveRaidBattleSession session, bool closeLocalBattleMap = true)
        => raidSettlements.Expire(new ClientRaidSettlementRuntime(session), closeLocalBattleMap);
}
