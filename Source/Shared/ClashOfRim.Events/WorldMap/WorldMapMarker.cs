using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed record WorldMapMarker(
    string MarkerId,
    WorldMapMarkerKind Kind,
    string OwnerUserId,
    string? ColonyId,
    string? WorldObjectId,
    string? MapUniqueId,
    string? SnapshotId,
    int Tile,
    string? Label,
    DateTimeOffset CreatedAtUtc,
    string? RelatedEventId,
    bool TradeEnabled,
    bool ReinforcementEnabled,
    RaidAvailabilitySummary? RaidAvailability = null,
    string? IconDefName = null,
    string? RelationKind = null,
    bool OwnerOnline = false,
    DateTimeOffset? OwnerLastSeenAtUtc = null,
    string? OwnerFactionName = null,
    IReadOnlyList<int>? PathTiles = null,
    ColonyAppearanceDto? Appearance = null,
    int TileLayerId = 0);
