using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal interface IRemoteWorldObjectView
{
    string MarkerId { get; }

    string OwnerUserId { get; }

    string OwnerColonyId { get; }

    string OwnerFactionName { get; }

    string RuntimeKind { get; }

    string SourceWorldObjectId { get; }

    string SourceLabel { get; }

    string SourceMapId { get; }

    string SourceSnapshotId { get; }

    string RelatedEventId { get; }

    string RelationKind { get; }

    bool OwnerOnline { get; }

    string OwnerLastSeenAtUtc { get; }

    bool CanTrade { get; }

    bool CanRaid { get; }

    bool CanReinforce { get; }

    string RaidUnavailableReason { get; }

    string RaidUnavailableUntilUtc { get; }

    PlanetTile Tile { get; }

    string Label { get; }

    WorldObject WorldObject { get; }
}
