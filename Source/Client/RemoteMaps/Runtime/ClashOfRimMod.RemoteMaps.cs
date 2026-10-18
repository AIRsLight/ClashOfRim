using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenCaravanRaidMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (targets.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoTargets");
            return;
        }

        IReadOnlyList<Pawn> pawns = new CaravanRaidAttackForceSource(caravan).AttackPawns;
        if (pawns.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (ModWorldMapMarkerDto target in targets)
        {
            ModWorldMapMarkerDto captured = target;
            options.Add(new FloatMenuOption(
                ClashOfRimText.Key(
                    "ClashOfRim.Raid.CaravanTargetOption",
                    FormatSupportTargetLabel(captured).Named("TARGET"),
                    pawns.Count.Named("COUNT")),
                () =>
                {
                    string confirmation = ClashOfRimText.Key(
                        "ClashOfRim.Raid.ConfirmStart",
                        FormatSupportTargetLabel(captured).Named("TARGET"),
                        pawns.Count.Named("COUNT"));
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        confirmation,
                        () => StartCreateRaidFromCaravan(caravan, captured)));
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    public void StartRaidFromVehicleLanding(Caravan caravan, ModWorldMapMarkerDto target)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (caravan is null || caravan.Destroyed)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidStartFailedNoCaravan");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (target is null || !target.CanRaid)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        StartCreateRaidFromCaravan(caravan, target);
    }

    private void StartCreateRaidFromCaravan(Caravan caravan, ModWorldMapMarkerDto target)
    {
        _ = raidLaunching.StartAsync(new ClientRaidLaunchRuntime(new CaravanRaidAttackForceSource(caravan)), target);
    }

    internal void StartCreateRaidFromTransporters(
        string transporterKey,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string targetMapId,
        string targetWorldObjectId,
        PlanetTile targetTile,
        int targetTileLayerId,
        string targetLabel,
        List<ActiveTransporterInfo> transporters)
    {
        var target = new ModWorldMapMarkerDto
        {
            MarkerId = string.Empty,
            Kind = "TradeableColony",
            OwnerUserId = targetUserId ?? string.Empty,
            OwnerColonyId = targetColonyId ?? string.Empty,
            SnapshotId = targetSnapshotId ?? string.Empty,
            MapId = targetMapId ?? string.Empty,
            WorldObjectId = targetWorldObjectId ?? string.Empty,
            Tile = targetTile,
            TileLayerId = Math.Max(0, targetTileLayerId),
            Label = targetLabel ?? string.Empty,
            CanRaid = true
        };
        _ = raidLaunching.StartAsync(new ClientRaidLaunchRuntime(new TransporterRaidAttackForceSource(
            transporters, target.Tile, transporterKey, target.TileLayerId)), target);
    }

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
        => observationMaps.OpenAsync(target, mode);

    internal void AdvanceRaidRecovery() { snapshotReload.Advance(); raidRecovery.Advance(); }
    internal bool RaidRecoveryBlocksBattleLifecycle => raidRecovery.BlocksBattleLifecycle || snapshotReload.BlocksGameLifecycle;

    internal void StartFinishActiveRaidBattle(ActiveRaidBattleSession session, string finishReason)
        => _ = raidSettlements.StartAsync(new ClientRaidSettlementRuntime(session), finishReason);

    internal void HandleRaidBattleFinalDeadlineExpired(ActiveRaidBattleSession session, bool closeLocalBattleMap = true)
        => raidSettlements.Expire(new ClientRaidSettlementRuntime(session), closeLocalBattleMap);
}
