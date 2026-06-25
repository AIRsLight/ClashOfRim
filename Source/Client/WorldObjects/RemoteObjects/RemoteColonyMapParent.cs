using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

public sealed class RemoteColonyMapParent : MapParent, IRemoteWorldObjectView
{
    public string MarkerId = string.Empty;
    public string OwnerUserId = string.Empty;
    public string OwnerColonyId = string.Empty;
    public string OwnerFactionName = string.Empty;
    public string RuntimeKind = string.Empty;
    public string SourceWorldObjectId = string.Empty;
    public string SourceDefName = string.Empty;
    public string SourceLabel = string.Empty;
    public string SourceMapId = string.Empty;
    public string SourceSnapshotId = string.Empty;
    public string RelatedEventId = string.Empty;
    public string RelationKind = string.Empty;
    public bool OwnerOnline;
    public string OwnerLastSeenAtUtc = string.Empty;
    public bool CanTrade;
    public bool CanRaid;
    public bool CanReinforce;
    public string RaidUnavailableReason = string.Empty;
    public string RaidUnavailableUntilUtc = string.Empty;
    public string AppearanceMode = string.Empty;
    public string AppearanceIconDefName = string.Empty;
    public string AppearanceColorDefName = string.Empty;
    public string AppearanceColorHex = string.Empty;

    public override string Label => BuildLabel();

    public override Material Material => RemoteColonyWorldIconCache.GetSettlementMaterial(
        AppearanceColorDefName,
        AppearanceColorHex,
        RelationKind);

    public override Texture2D ExpandingIcon => RemoteColonyWorldIconCache.GetTexture(
        AppearanceMode,
        AppearanceIconDefName,
        AppearanceColorDefName,
        AppearanceColorHex,
        RelationKind);

    public override Color ExpandingIconColor => Color.white;

    string IRemoteWorldObjectView.MarkerId => MarkerId;
    string IRemoteWorldObjectView.OwnerUserId => OwnerUserId;
    string IRemoteWorldObjectView.OwnerColonyId => OwnerColonyId;
    string IRemoteWorldObjectView.OwnerFactionName => OwnerFactionName;
    string IRemoteWorldObjectView.RuntimeKind => RuntimeKind;
    string IRemoteWorldObjectView.SourceWorldObjectId => SourceWorldObjectId;
    string IRemoteWorldObjectView.SourceLabel => SourceLabel;
    string IRemoteWorldObjectView.SourceMapId => SourceMapId;
    string IRemoteWorldObjectView.SourceSnapshotId => SourceSnapshotId;
    string IRemoteWorldObjectView.RelatedEventId => RelatedEventId;
    string IRemoteWorldObjectView.RelationKind => RelationKind;
    bool IRemoteWorldObjectView.OwnerOnline => OwnerOnline;
    string IRemoteWorldObjectView.OwnerLastSeenAtUtc => OwnerLastSeenAtUtc;
    bool IRemoteWorldObjectView.CanTrade => CanTrade;
    bool IRemoteWorldObjectView.CanRaid => CanRaid;
    bool IRemoteWorldObjectView.CanReinforce => CanReinforce;
    string IRemoteWorldObjectView.RaidUnavailableReason => RaidUnavailableReason;
    string IRemoteWorldObjectView.RaidUnavailableUntilUtc => RaidUnavailableUntilUtc;
    PlanetTile IRemoteWorldObjectView.Tile => Tile;
    string IRemoteWorldObjectView.Label => Label;
    WorldObject IRemoteWorldObjectView.WorldObject => this;

    public override void ExposeData()
    {
        base.ExposeData();
#pragma warning disable CS8601
        Scribe_Values.Look(ref MarkerId, "clashOfRimMarkerId");
        Scribe_Values.Look(ref OwnerUserId, "clashOfRimOwnerUserId");
        Scribe_Values.Look(ref OwnerColonyId, "clashOfRimOwnerColonyId");
        Scribe_Values.Look(ref OwnerFactionName, "clashOfRimOwnerFactionName");
        Scribe_Values.Look(ref RuntimeKind, "clashOfRimRuntimeKind");
        Scribe_Values.Look(ref SourceWorldObjectId, "clashOfRimSourceWorldObjectId");
        Scribe_Values.Look(ref SourceDefName, "clashOfRimSourceDefName");
        Scribe_Values.Look(ref SourceLabel, "clashOfRimSourceLabel");
        Scribe_Values.Look(ref SourceMapId, "clashOfRimSourceMapId");
        Scribe_Values.Look(ref SourceSnapshotId, "clashOfRimSourceSnapshotId");
        Scribe_Values.Look(ref RelatedEventId, "clashOfRimRelatedEventId");
        Scribe_Values.Look(ref RelationKind, "clashOfRimRelationKind");
        Scribe_Values.Look(ref OwnerOnline, "clashOfRimOwnerOnline");
        Scribe_Values.Look(ref OwnerLastSeenAtUtc, "clashOfRimOwnerLastSeenAtUtc");
        Scribe_Values.Look(ref CanTrade, "clashOfRimCanTrade");
        Scribe_Values.Look(ref CanRaid, "clashOfRimCanRaid");
        Scribe_Values.Look(ref CanReinforce, "clashOfRimCanReinforce");
        Scribe_Values.Look(ref RaidUnavailableReason, "clashOfRimRaidUnavailableReason");
        Scribe_Values.Look(ref RaidUnavailableUntilUtc, "clashOfRimRaidUnavailableUntilUtc");
        Scribe_Values.Look(ref AppearanceMode, "clashOfRimAppearanceMode");
        Scribe_Values.Look(ref AppearanceIconDefName, "clashOfRimAppearanceIconDefName");
        Scribe_Values.Look(ref AppearanceColorDefName, "clashOfRimAppearanceColorDefName");
        Scribe_Values.Look(ref AppearanceColorHex, "clashOfRimAppearanceColorHex");
#pragma warning restore CS8601
        MarkerId ??= string.Empty;
        OwnerUserId ??= string.Empty;
        OwnerColonyId ??= string.Empty;
        OwnerFactionName ??= string.Empty;
        RuntimeKind ??= string.Empty;
        SourceWorldObjectId ??= string.Empty;
        SourceDefName ??= string.Empty;
        SourceLabel ??= string.Empty;
        SourceMapId ??= string.Empty;
        SourceSnapshotId ??= string.Empty;
        RelatedEventId ??= string.Empty;
        RelationKind ??= string.Empty;
        OwnerLastSeenAtUtc ??= string.Empty;
        RaidUnavailableReason ??= string.Empty;
        RaidUnavailableUntilUtc ??= string.Empty;
        AppearanceMode ??= string.Empty;
        AppearanceIconDefName ??= string.Empty;
        AppearanceColorDefName ??= string.Empty;
        AppearanceColorHex ??= string.Empty;
    }

    public void Configure(
        string markerId,
        string ownerUserId,
        string? ownerColonyId,
        string? ownerFactionName,
        string kind,
        string worldObjectId,
        string? defName,
        string? label,
        string? mapId,
        string? snapshotId,
        string? relatedEventId,
        string? relationKind,
        bool ownerOnline,
        string? ownerLastSeenAtUtc,
        bool canTrade,
        bool canRaid,
        bool canReinforce,
        string? raidUnavailableReason,
        string? raidUnavailableUntilUtc,
        string? appearanceMode,
        string? appearanceIconDefName,
        string? appearanceColorDefName,
        string? appearanceColorHex)
    {
        MarkerId = markerId;
        OwnerUserId = ownerUserId;
        OwnerColonyId = ownerColonyId ?? string.Empty;
        OwnerFactionName = ownerFactionName ?? string.Empty;
        RuntimeKind = kind;
        SourceWorldObjectId = worldObjectId;
        SourceDefName = defName ?? string.Empty;
        SourceLabel = label ?? string.Empty;
        SourceMapId = mapId ?? string.Empty;
        SourceSnapshotId = snapshotId ?? string.Empty;
        RelatedEventId = relatedEventId ?? string.Empty;
        RelationKind = relationKind ?? string.Empty;
        OwnerOnline = ownerOnline;
        OwnerLastSeenAtUtc = ownerLastSeenAtUtc ?? string.Empty;
        CanTrade = canTrade;
        CanRaid = canRaid;
        CanReinforce = canReinforce;
        RaidUnavailableReason = raidUnavailableReason ?? string.Empty;
        RaidUnavailableUntilUtc = raidUnavailableUntilUtc ?? string.Empty;
        AppearanceMode = appearanceMode ?? string.Empty;
        AppearanceIconDefName = appearanceIconDefName ?? string.Empty;
        AppearanceColorDefName = appearanceColorDefName ?? string.Empty;
        AppearanceColorHex = appearanceColorHex ?? string.Empty;
    }

    public override string GetInspectString()
    {
        return RemoteWorldObjectUiUtility.BuildInspectString(this, base.GetInspectString());
    }

    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (Gizmo gizmo in RemoteWorldObjectUiUtility.BuildGizmos(this, base.GetGizmos(), useRuntimeActionNotice: false))
        {
            yield return gizmo;
        }
    }

    public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(
        IEnumerable<IThingHolder> pods,
        System.Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (FloatMenuOption option in RemoteWorldObjectUiUtility.BuildTransportersFloatMenuOptions(this, pods, launchAction))
        {
            yield return option;
        }
    }

    public override IEnumerable<FloatMenuOption> GetShuttleFloatMenuOptions(
        IEnumerable<IThingHolder> pods,
        System.Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (FloatMenuOption option in RemoteWorldObjectUiUtility.BuildTransportersFloatMenuOptions(this, pods, launchAction))
        {
            yield return option;
        }
    }

    private string BuildLabel()
    {
        return RemoteWorldObjectUiUtility.BuildLabel(this, useRuntimeKindFallback: false);
    }
}
