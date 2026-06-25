using System;
using System.Collections.Generic;
using System.Linq;
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
    {
        targetUserId = target.OwnerUserId ?? string.Empty;
        targetColonyId = target.OwnerColonyId ?? string.Empty;
        targetSnapshotId = target.SourceSnapshotId ?? string.Empty;
        targetMapId = target.SourceMapId ?? string.Empty;
        targetWorldObjectId = target.SourceWorldObjectId ?? string.Empty;
        targetLabel = target.Label ?? string.Empty;
        targetTileLayerId = ReadTileLayerId(target.Tile);
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

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod?.PvpEnabled != true)
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled"));
        }

        if (!HasTargetContext() || !canRaid)
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetIncomplete"));
        }

        IReadOnlyList<IThingHolder> podList = pods as IReadOnlyList<IThingHolder> ?? pods.ToList();
        return podList.Count > 0 && TransportersArrivalActionUtility.AnyNonDownedColonist(podList)
            ? FloatMenuAcceptanceReport.WasAccepted
            : FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.Raid.TransportPodRejectNoColonist"));
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

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        mod.StartCreateRaidFromTransporters(
            transporterKey,
            targetUserId,
            targetColonyId,
            targetSnapshotId,
            targetMapId,
            targetWorldObjectId,
            tile,
            ReadTileLayerId(tile),
            targetLabel,
            transporters);
    }

    private bool HasTargetContext()
    {
        return !string.IsNullOrWhiteSpace(targetUserId)
            && !string.IsNullOrWhiteSpace(targetColonyId)
            && !string.IsNullOrWhiteSpace(targetMapId)
            && !string.IsNullOrWhiteSpace(targetWorldObjectId);
    }

    private static int ReadTileLayerId(PlanetTile tile)
    {
        try
        {
            return tile.Valid ? Math.Max(0, tile.Layer.LayerID) : 0;
        }
        catch
        {
            return 0;
        }
    }
}
