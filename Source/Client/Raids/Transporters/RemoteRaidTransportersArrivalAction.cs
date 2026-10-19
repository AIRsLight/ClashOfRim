using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RemoteRaidTransportersArrivalAction : TransportersArrivalAction
{
    private string targetUserId = string.Empty;
    private string targetColonyId = string.Empty;
    private string targetSnapshotId = string.Empty;
    private string targetMapId = string.Empty;
    private string targetWorldObjectId = string.Empty;
    private string targetLabel = string.Empty;
    private string transporterKey = string.Empty;
    private int targetTileLayerId;
    private bool canRaid;

    public RemoteRaidTransportersArrivalAction()
    {
    }

    internal RemoteRaidTransportersArrivalAction(IRemoteWorldObjectView target)
        : this(RemoteWorldObjectUiUtility.CaptureTarget(target), target.Label) { }

    internal RemoteRaidTransportersArrivalAction(ModWorldMapMarkerDto target, string label)
    {
        targetUserId = target.OwnerUserId ?? string.Empty;
        targetColonyId = target.OwnerColonyId ?? string.Empty;
        targetSnapshotId = target.SnapshotId ?? string.Empty;
        targetMapId = target.MapId ?? string.Empty;
        targetWorldObjectId = target.WorldObjectId ?? string.Empty;
        targetLabel = label ?? string.Empty;
        targetTileLayerId = target.TileLayerId;
        transporterKey = Guid.NewGuid().ToString("N");
        canRaid = target.CanRaid;
    }

    public override bool GeneratesMap => false;

    public override void ExposeData()
    {
        base.ExposeData();
#pragma warning disable CS8601
        Scribe_Values.Look(ref targetUserId, "targetUserId");
        Scribe_Values.Look(ref targetColonyId, "targetColonyId");
        Scribe_Values.Look(ref targetSnapshotId, "targetSnapshotId");
        Scribe_Values.Look(ref targetMapId, "targetMapId");
        Scribe_Values.Look(ref targetWorldObjectId, "targetWorldObjectId");
        Scribe_Values.Look(ref targetLabel, "targetLabel");
        Scribe_Values.Look(ref transporterKey, "transporterKey");
        Scribe_Values.Look(ref targetTileLayerId, "targetTileLayerId", defaultValue: 0);
        Scribe_Values.Look(ref canRaid, "canRaid", defaultValue: false);
#pragma warning restore CS8601
        targetUserId ??= string.Empty;
        targetColonyId ??= string.Empty;
        targetSnapshotId ??= string.Empty;
        targetMapId ??= string.Empty;
        targetWorldObjectId ??= string.Empty;
        targetLabel ??= string.Empty;
        transporterKey ??= string.Empty;
    }

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
    {
        FloatMenuAcceptanceReport baseReport = base.StillValid(pods, destinationTile);
        if (!baseReport)
        {
            return baseReport;
        }

        return ClientTransporterActions.Current?.RaidAcceptance(Target(destinationTile), pods)
            ?? ClientTransporterActions.Reject("ClashOfRim.Raid.PvpDisabled");
    }

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        if (string.IsNullOrWhiteSpace(transporterKey))
        {
            transporterKey = Guid.NewGuid().ToString("N");
        }

        FloatMenuAcceptanceReport report = StillValid(transporters, tile);
        if (!report.Accepted)
        {
            Messages.Message(
                report.FailMessage.NullOrEmpty() ? report.FailReason : report.FailMessage,
                new GlobalTargetInfo(tile),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        ClientTransporterActions.Current!.StartRaid(transporterKey, Target(tile), transporters);
    }

    private ModWorldMapMarkerDto Target(PlanetTile tile)
    {
        var target = ClientRaidCommands.TransporterTarget(targetUserId, targetColonyId, targetSnapshotId, targetMapId,
            targetWorldObjectId, tile, RemoteWorldObjectUiUtility.ReadTileLayerId(tile), targetLabel);
        target.CanRaid = canRaid;
        return target;
    }
}
