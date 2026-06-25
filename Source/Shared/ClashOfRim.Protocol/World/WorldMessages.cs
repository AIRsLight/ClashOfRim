using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class WorldMapMarkerDto
{
    public WorldMapMarkerDto(
        string markerId,
        string kind,
        string ownerUserId,
        string? ownerColonyId,
        string worldObjectId,
        string? mapId,
        string? snapshotId,
        int tile,
        string? label,
        string? relatedEventId,
        bool canRaid,
        bool canTrade,
        bool canReinforce,
        string? raidUnavailableReason = null,
        DateTimeOffset? raidUnavailableUntilUtc = null,
        string? iconDefName = null,
        string? relationKind = null,
        bool ownerOnline = false,
        DateTimeOffset? ownerLastSeenAtUtc = null,
        string? ownerFactionName = null,
        IReadOnlyList<int>? pathTiles = null,
        ColonyAppearanceDto? appearance = null,
        int tileLayerId = 0)
    {
        MarkerId = markerId;
        Kind = kind;
        OwnerUserId = ownerUserId;
        OwnerColonyId = ownerColonyId;
        WorldObjectId = worldObjectId;
        MapId = mapId;
        SnapshotId = snapshotId;
        Tile = tile;
        Label = label;
        RelatedEventId = relatedEventId;
        CanRaid = canRaid;
        CanTrade = canTrade;
        CanReinforce = canReinforce;
        RaidUnavailableReason = raidUnavailableReason;
        RaidUnavailableUntilUtc = raidUnavailableUntilUtc;
        IconDefName = iconDefName;
        RelationKind = relationKind;
        OwnerOnline = ownerOnline;
        OwnerLastSeenAtUtc = ownerLastSeenAtUtc;
        OwnerFactionName = ownerFactionName;
        PathTiles = pathTiles ?? Array.Empty<int>();
        Appearance = appearance;
        TileLayerId = Math.Max(0, tileLayerId);
    }

    public string MarkerId { get; }

    public string Kind { get; }

    public string OwnerUserId { get; }

    public string? OwnerColonyId { get; }

    public string WorldObjectId { get; }

    public string? MapId { get; }

    public string? SnapshotId { get; }

    public int Tile { get; }

    public int TileLayerId { get; }

    public string? Label { get; }

    public string? RelatedEventId { get; }

    public bool CanRaid { get; }

    public bool CanTrade { get; }

    public bool CanReinforce { get; }

    public string? RaidUnavailableReason { get; }

    public DateTimeOffset? RaidUnavailableUntilUtc { get; }

    public string? IconDefName { get; }

    public string? RelationKind { get; }

    public bool OwnerOnline { get; }

    public DateTimeOffset? OwnerLastSeenAtUtc { get; }

    public string? OwnerFactionName { get; }

    public IReadOnlyList<int> PathTiles { get; }

    public ColonyAppearanceDto? Appearance { get; }
}

public sealed class SyncWorldMapMarkersRequest
{
    public SyncWorldMapMarkersRequest(string userId, DateTimeOffset knownAtUtc, string? colonyId = null)
    {
        UserId = userId;
        KnownAtUtc = knownAtUtc;
        ColonyId = colonyId;
    }

    public string UserId { get; }

    public string? ColonyId { get; }

    public DateTimeOffset KnownAtUtc { get; }
}

public sealed class WorldMapMarkerDeliveryDto
{
    public WorldMapMarkerDeliveryDto(
        string userId,
        DateTimeOffset generatedAtUtc,
        IReadOnlyList<WorldMapMarkerDto> markers,
        bool giftsEnabled = true,
        bool pvpEnabled = true)
    {
        UserId = userId;
        GeneratedAtUtc = generatedAtUtc;
        Markers = markers;
        GiftsEnabled = giftsEnabled;
        PvpEnabled = pvpEnabled;
    }

    public string UserId { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyList<WorldMapMarkerDto> Markers { get; }

    public bool GiftsEnabled { get; }

    public bool PvpEnabled { get; }
}

public sealed class RuntimeWorldObjectMarkerDto
{
    public RuntimeWorldObjectMarkerDto(
        string worldObjectId,
        string? defName,
        string kind,
        int tile,
        string? label,
        IReadOnlyList<int>? pathTiles = null,
        int tileLayerId = 0)
    {
        WorldObjectId = worldObjectId;
        DefName = defName;
        Kind = kind;
        Tile = tile;
        TileLayerId = Math.Max(0, tileLayerId);
        Label = label;
        PathTiles = pathTiles ?? Array.Empty<int>();
    }

    public string WorldObjectId { get; }

    public string? DefName { get; }

    public string Kind { get; }

    public int Tile { get; }

    public int TileLayerId { get; }

    public string? Label { get; }

    public IReadOnlyList<int> PathTiles { get; }
}

public sealed class SyncRuntimeWorldObjectsRequest
{
    public SyncRuntimeWorldObjectsRequest(
        string userId,
        string colonyId,
        string? snapshotId,
        DateTimeOffset sentAtUtc,
        IReadOnlyList<RuntimeWorldObjectMarkerDto>? objects,
        string? authToken = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        SnapshotId = snapshotId;
        SentAtUtc = sentAtUtc;
        Objects = objects ?? Array.Empty<RuntimeWorldObjectMarkerDto>();
        AuthToken = authToken;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? SnapshotId { get; }

    public DateTimeOffset SentAtUtc { get; }

    public IReadOnlyList<RuntimeWorldObjectMarkerDto> Objects { get; }

    public string? AuthToken { get; }
}

public sealed class SyncRuntimeWorldObjectsResponse
{
    public SyncRuntimeWorldObjectsResponse(
        ProtocolResponse result,
        string userId,
        DateTimeOffset acceptedAtUtc,
        int acceptedCount,
        WorldMapMarkerDeliveryDto? worldMapMarkers)
    {
        Result = result;
        UserId = userId;
        AcceptedAtUtc = acceptedAtUtc;
        AcceptedCount = acceptedCount;
        WorldMapMarkers = worldMapMarkers;
    }

    public ProtocolResponse Result { get; }

    public string UserId { get; }

    public DateTimeOffset AcceptedAtUtc { get; }

    public int AcceptedCount { get; }

    public WorldMapMarkerDeliveryDto? WorldMapMarkers { get; }
}

public sealed class ServerNotificationDto
{
    public ServerNotificationDto(string notificationId, string title, string text, string severity, bool fromAdministrator)
    {
        NotificationId = notificationId;
        Title = title;
        Text = text;
        Severity = severity;
        FromAdministrator = fromAdministrator;
    }

    public string NotificationId { get; }

    public string Title { get; }

    public string Text { get; }

    public string Severity { get; }

    public bool FromAdministrator { get; }
}
