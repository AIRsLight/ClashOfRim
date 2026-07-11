using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

public sealed class RemoteGiftTransportersArrivalAction : TransportersArrivalAction
{
    private string markerId = string.Empty;
    private string targetUserId = string.Empty;
    private string targetColonyId = string.Empty;
    private string targetSnapshotId = string.Empty;
    private string targetMapId = string.Empty;
    private string targetWorldObjectId = string.Empty;
    private string targetLabel = string.Empty;
    private string transporterKey = string.Empty;
    private bool forcedDelivery;

    public RemoteGiftTransportersArrivalAction()
    {
    }

    public RemoteGiftTransportersArrivalAction(RemoteRuntimeWorldObject target, bool forcedDelivery)
    {
        markerId = target.MarkerId ?? string.Empty;
        targetUserId = target.OwnerUserId ?? string.Empty;
        targetColonyId = target.OwnerColonyId ?? string.Empty;
        targetSnapshotId = target.SourceSnapshotId ?? string.Empty;
        targetMapId = target.SourceMapId ?? string.Empty;
        targetWorldObjectId = target.SourceWorldObjectId ?? string.Empty;
        targetLabel = target.Label ?? string.Empty;
        transporterKey = Guid.NewGuid().ToString("N");
        this.forcedDelivery = forcedDelivery;
    }

    public RemoteGiftTransportersArrivalAction(RemoteColonyMapParent target, bool forcedDelivery)
    {
        markerId = target.MarkerId ?? string.Empty;
        targetUserId = target.OwnerUserId ?? string.Empty;
        targetColonyId = target.OwnerColonyId ?? string.Empty;
        targetSnapshotId = target.SourceSnapshotId ?? string.Empty;
        targetMapId = target.SourceMapId ?? string.Empty;
        targetWorldObjectId = target.SourceWorldObjectId ?? string.Empty;
        targetLabel = target.Label ?? string.Empty;
        transporterKey = Guid.NewGuid().ToString("N");
        this.forcedDelivery = forcedDelivery;
    }

    internal RemoteGiftTransportersArrivalAction(IRemoteWorldObjectView target, bool forcedDelivery)
    {
        markerId = target.MarkerId ?? string.Empty;
        targetUserId = target.OwnerUserId ?? string.Empty;
        targetColonyId = target.OwnerColonyId ?? string.Empty;
        targetSnapshotId = target.SourceSnapshotId ?? string.Empty;
        targetMapId = target.SourceMapId ?? string.Empty;
        targetWorldObjectId = target.SourceWorldObjectId ?? string.Empty;
        targetLabel = target.Label ?? string.Empty;
        transporterKey = Guid.NewGuid().ToString("N");
        this.forcedDelivery = forcedDelivery;
    }

    public override bool GeneratesMap => false;

    public override void ExposeData()
    {
        base.ExposeData();
#pragma warning disable CS8601
        Scribe_Values.Look(ref markerId, "markerId");
        Scribe_Values.Look(ref targetUserId, "targetUserId");
        Scribe_Values.Look(ref targetColonyId, "targetColonyId");
        Scribe_Values.Look(ref targetSnapshotId, "targetSnapshotId");
        Scribe_Values.Look(ref targetMapId, "targetMapId");
        Scribe_Values.Look(ref targetWorldObjectId, "targetWorldObjectId");
        Scribe_Values.Look(ref targetLabel, "targetLabel");
        Scribe_Values.Look(ref transporterKey, "transporterKey");
        Scribe_Values.Look(ref forcedDelivery, "forcedDelivery", defaultValue: false);
#pragma warning restore CS8601
        markerId ??= string.Empty;
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

        if (!HasTargetContext())
        {
            return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusTargetIncomplete"));
        }

        return GiftTransporterPayloadUtility.CanSend(
            pods,
            forcedDelivery ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift);
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
        IReadOnlyList<ModThingReferenceDto> things = GiftTransporterPayloadUtility.BuildReferences(
            transporters,
            mod.UserId,
            mod.ColonyId,
            mod.CurrentSnapshotId,
            transporterKey,
            forcedDelivery);
        mod.StartCreateGiftFromTransportPods(
            transporterKey,
            targetUserId,
            targetColonyId,
            targetSnapshotId,
            targetMapId,
            targetWorldObjectId,
            tile,
            targetLabel,
            things,
            forcedDelivery);
    }

    private bool HasTargetContext()
    {
        return !string.IsNullOrWhiteSpace(targetUserId)
            && !string.IsNullOrWhiteSpace(targetColonyId)
            && !string.IsNullOrWhiteSpace(targetMapId);
    }
}
