using AIRsLight.ClashOfRim.Compatibility;
using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModSyncWorldMapMarkersRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "knownAtUtc")]
    public string KnownAtUtc { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModWorldMapMarkerDto
{
    [DataMember(Name = "markerId")]
    public string MarkerId { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "ownerUserId")]
    public string OwnerUserId { get; set; } = string.Empty;

    [DataMember(Name = "ownerColonyId")]
    public string? OwnerColonyId { get; set; }

    [DataMember(Name = "worldObjectId")]
    public string WorldObjectId { get; set; } = string.Empty;

    [DataMember(Name = "mapId")]
    public string? MapId { get; set; }

    [DataMember(Name = "snapshotId")]
    public string? SnapshotId { get; set; }

    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "tileLayerId")]
    public int TileLayerId { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "relatedEventId")]
    public string? RelatedEventId { get; set; }

    [DataMember(Name = "canRaid")]
    public bool CanRaid { get; set; }

    [DataMember(Name = "canTrade")]
    public bool CanTrade { get; set; }

    [DataMember(Name = "canReinforce")]
    public bool CanReinforce { get; set; }

    [DataMember(Name = "raidUnavailableReason")]
    public string? RaidUnavailableReason { get; set; }

    [DataMember(Name = "raidUnavailableUntilUtc")]
    public string? RaidUnavailableUntilUtc { get; set; }

    [DataMember(Name = "iconDefName")]
    public string? IconDefName { get; set; }

    [DataMember(Name = "relationKind")]
    public string? RelationKind { get; set; }

    [DataMember(Name = "ownerOnline")]
    public bool OwnerOnline { get; set; }

    [DataMember(Name = "ownerLastSeenAtUtc")]
    public string? OwnerLastSeenAtUtc { get; set; }

    [DataMember(Name = "ownerFactionName")]
    public string? OwnerFactionName { get; set; }

    [DataMember(Name = "pathTiles")]
    public List<int>? PathTiles { get; set; }

    [DataMember(Name = "appearance")]
    public ModColonyAppearanceDto? Appearance { get; set; }
}

[DataContract]
public sealed class ModColonyAppearanceDto
{
    [DataMember(Name = "mode")]
    public string? Mode { get; set; }

    [DataMember(Name = "iconDefName")]
    public string? IconDefName { get; set; }

    [DataMember(Name = "colorDefName")]
    public string? ColorDefName { get; set; }

    [DataMember(Name = "colorHex")]
    public string? ColorHex { get; set; }
}

[DataContract]
public sealed class ModWorldMapMarkerDeliveryDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "giftsEnabled")]
    public bool GiftsEnabled { get; set; } = true;

    [DataMember(Name = "pvpEnabled")]
    public bool PvpEnabled { get; set; } = true;

    [DataMember(Name = "markers")]
    public List<ModWorldMapMarkerDto> Markers { get; set; } = new();
}

[DataContract]
public sealed class ModRuntimeWorldObjectMarkerDto
{
    [DataMember(Name = "worldObjectId")]
    public string WorldObjectId { get; set; } = string.Empty;

    [DataMember(Name = "defName")]
    public string? DefName { get; set; }

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "tileLayerId")]
    public int TileLayerId { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "pathTiles")]
    public List<int>? PathTiles { get; set; }
}

[DataContract]
public sealed class ModSyncRuntimeWorldObjectsRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "snapshotId")]
    public string? SnapshotId { get; set; }

    [DataMember(Name = "sentAtUtc")]
    public string SentAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "objects")]
    public List<ModRuntimeWorldObjectMarkerDto> Objects { get; set; } = new();

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModSyncRuntimeWorldObjectsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "acceptedAtUtc")]
    public string AcceptedAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "acceptedCount")]
    public int AcceptedCount { get; set; }

    [DataMember(Name = "worldMapMarkers")]
    public ModWorldMapMarkerDeliveryDto? WorldMapMarkers { get; set; }
}

[DataContract]
public sealed class ModPrepareWorldSessionRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "compatibilityManifestJson")]
    public string? CompatibilityManifestJson { get; set; }

    [DataMember(Name = "compatibilityManifestId")]
    public string? CompatibilityManifestId { get; set; }

    [DataMember(Name = "compatibilityManifestSummaryJson")]
    public string? CompatibilityManifestSummaryJson { get; set; }

    [DataMember(Name = "steamAuthTicket")]
    public string? SteamAuthTicket { get; set; }

    [DataMember(Name = "password")]
    public string? Password { get; set; }
}

[DataContract]
public sealed class ModPrepareWorldSessionResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "worldConfigured")]
    public bool WorldConfigured { get; set; }

    [DataMember(Name = "requiresInitialWorldConfiguration")]
    public bool RequiresInitialWorldConfiguration { get; set; }

    [DataMember(Name = "administratorUserId")]
    public string? AdministratorUserId { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }

    [DataMember(Name = "hasExistingColony")]
    public bool HasExistingColony { get; set; }

    [DataMember(Name = "latestSnapshotId")]
    public string? LatestSnapshotId { get; set; }

    [DataMember(Name = "serverCompatibilityManifestJson")]
    public string? ServerCompatibilityManifestJson { get; set; }

    [DataMember(Name = "compatibilityIssues")]
    public List<ModCompatibilityIssueDto> CompatibilityIssues { get; set; } = new();

    [DataMember(Name = "canOverrideCompatibilityBaseline")]
    public bool CanOverrideCompatibilityBaseline { get; set; }

    [DataMember(Name = "assignedColonyId")]
    public string? AssignedColonyId { get; set; }

    [DataMember(Name = "requiresFullCompatibilityManifest")]
    public bool RequiresFullCompatibilityManifest { get; set; }

    [DataMember(Name = "requestedCompatibilityPackageIds")]
    public List<string> RequestedCompatibilityPackageIds { get; set; } = new();
}

[DataContract]
public sealed class ModSubmitWorldConfigurationRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "configuration")]
    public ModWorldConfigurationDto? Configuration { get; set; }
}

[DataContract]
public sealed class ModGetWorldConfigurationRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "steamAuthTicket")]
    public string? SteamAuthTicket { get; set; }

    [DataMember(Name = "password")]
    public string? Password { get; set; }

    [DataMember(Name = "includeGenerationBaseline")]
    public bool IncludeGenerationBaseline { get; set; } = true;

    [DataMember(Name = "includePlayerColonySites")]
    public bool IncludePlayerColonySites { get; set; } = true;

    [DataMember(Name = "includeWorldExtensions")]
    public bool IncludeWorldExtensions { get; set; } = true;
}

[DataContract]
public sealed class ModGetWorldConfigurationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }
}

[DataContract]
public sealed class ModSubmitWorldConfigurationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "worldConfigured")]
    public bool WorldConfigured { get; set; }

    [DataMember(Name = "administratorUserId")]
    public string? AdministratorUserId { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }
}

[DataContract]
public sealed class ModRegisterPlayerColonySitesRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "sites")]
    public List<ModPlayerColonySiteDto> Sites { get; set; } = new();

    [DataMember(Name = "extensions")]
    public List<ModWorldConfigurationExtensionDto> Extensions { get; set; } = new();

    [DataMember(Name = "suppressWorldConfigurationNotification")]
    public bool SuppressWorldConfigurationNotification { get; set; }
}

[DataContract]
public sealed class ModRegisterPlayerColonySitesResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "acceptedCount")]
    public int AcceptedCount { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }

    [DataMember(Name = "worldMapMarkers")]
    public ModWorldMapMarkerDeliveryDto? WorldMapMarkers { get; set; }
}

[DataContract]
public sealed class ModPreflightColonyRelocationRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "targetTile")]
    public int TargetTile { get; set; }

    [DataMember(Name = "targetTileLayerId")]
    public int TargetTileLayerId { get; set; }

    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModConfirmColonyRelocationRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "previousSnapshotId")]
    public string PreviousSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "relocatedSnapshotId")]
    public string RelocatedSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "targetTile")]
    public int TargetTile { get; set; }

    [DataMember(Name = "targetTileLayerId")]
    public int TargetTileLayerId { get; set; }

    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModColonyRelocationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "oldSite")]
    public ModPlayerColonySiteDto? OldSite { get; set; }

    [DataMember(Name = "newSite")]
    public ModPlayerColonySiteDto? NewSite { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }

    [DataMember(Name = "worldMapMarkers")]
    public ModWorldMapMarkerDeliveryDto? WorldMapMarkers { get; set; }
}

[DataContract]
public sealed class ModAbandonPlayerColonyRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModAbandonPlayerColonyResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "removedSnapshots")]
    public int RemovedSnapshots { get; set; }

    [DataMember(Name = "removedSites")]
    public int RemovedSites { get; set; }

    [DataMember(Name = "removedEvents")]
    public int RemovedEvents { get; set; }

    [DataMember(Name = "removedRuntimeMarkers")]
    public int RemovedRuntimeMarkers { get; set; }

    [DataMember(Name = "worldConfiguration")]
    public ModWorldConfigurationDto? WorldConfiguration { get; set; }

    [DataMember(Name = "worldMapMarkers")]
    public ModWorldMapMarkerDeliveryDto? WorldMapMarkers { get; set; }
}

[DataContract]
public sealed class ModWorldConfigurationDto
{
    [DataMember(Name = "worldConfigurationId")]
    public string WorldConfigurationId { get; set; } = string.Empty;

    [DataMember(Name = "configuredByUserId")]
    public string ConfiguredByUserId { get; set; } = string.Empty;

    [DataMember(Name = "configuredByColonyId")]
    public string ConfiguredByColonyId { get; set; } = string.Empty;

    [DataMember(Name = "configuredAtUtc")]
    public string ConfiguredAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "seedString")]
    public string? SeedString { get; set; }

    [DataMember(Name = "planetCoverage")]
    public string? PlanetCoverage { get; set; }

    [DataMember(Name = "overallRainfall")]
    public string? OverallRainfall { get; set; }

    [DataMember(Name = "overallTemperature")]
    public string? OverallTemperature { get; set; }

    [DataMember(Name = "overallPopulation")]
    public string? OverallPopulation { get; set; }

    [DataMember(Name = "landmarkDensity")]
    public string? LandmarkDensity { get; set; }

    [DataMember(Name = "tileCount")]
    public string? TileCount { get; set; }

    [DataMember(Name = "storytellerDefName")]
    public string? StorytellerDefName { get; set; }

    [DataMember(Name = "difficultyDefName")]
    public string? DifficultyDefName { get; set; }

    [DataMember(Name = "difficultyValuesXml")]
    public string? DifficultyValuesXml { get; set; }

    [DataMember(Name = "factionDefNames")]
    public List<string> FactionDefNames { get; set; } = new();

    [DataMember(Name = "features")]
    public List<ModWorldFeatureDto> Features { get; set; } = new();

    [DataMember(Name = "factions")]
    public List<ModWorldFactionDto> Factions { get; set; } = new();

    [DataMember(Name = "roads")]
    public List<ModWorldRoadDto> Roads { get; set; } = new();

    [DataMember(Name = "worldObjects")]
    public List<ModWorldObjectBaselineDto> WorldObjects { get; set; } = new();

    [DataMember(Name = "playerColonySites")]
    public List<ModPlayerColonySiteDto> PlayerColonySites { get; set; } = new();

    [DataMember(Name = "tileGeometry")]
    public ModWorldTileGeometryDto? TileGeometry { get; set; }

    [DataMember(Name = "extensions")]
    public List<ModWorldConfigurationExtensionDto> Extensions { get; set; } = new();
}

[DataContract]
public sealed class ModWorldConfigurationExtensionDto
{
    [DataMember(Name = "providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "schemaVersion")]
    public string SchemaVersion { get; set; } = string.Empty;

    [DataMember(Name = "payloadJson")]
    public string? PayloadJson { get; set; }

    [DataMember(Name = "metadata")]
    public Dictionary<string, string?> Metadata { get; set; } = new();
}

[DataContract]
public sealed class ModWorldTileGeometryDto
{
    [DataMember(Name = "layers")]
    public List<ModWorldTileLayerGeometryDto> Layers { get; set; } = new();
}

[DataContract]
public sealed class ModWorldTileLayerGeometryDto
{
    [DataMember(Name = "layerId")]
    public int LayerId { get; set; }

    [DataMember(Name = "layerDefName")]
    public string? LayerDefName { get; set; }

    [DataMember(Name = "averageTileSize")]
    public float AverageTileSize { get; set; }

    [DataMember(Name = "tileCenters")]
    public List<ModWorldTileCenterDto> TileCenters { get; set; } = new();
}

[DataContract]
public sealed class ModWorldTileCenterDto
{
    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "x")]
    public float X { get; set; }

    [DataMember(Name = "y")]
    public float Y { get; set; }

    [DataMember(Name = "z")]
    public float Z { get; set; }
}

[DataContract]
public sealed class ModWorldFeatureDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "maxDrawSizeInTiles")]
    public float MaxDrawSizeInTiles { get; set; }

    [DataMember(Name = "drawCenterX")]
    public float DrawCenterX { get; set; }

    [DataMember(Name = "drawCenterY")]
    public float DrawCenterY { get; set; }

    [DataMember(Name = "drawCenterZ")]
    public float DrawCenterZ { get; set; }
}

[DataContract]
public sealed class ModWorldFactionDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "colorR")]
    public float ColorR { get; set; }

    [DataMember(Name = "colorG")]
    public float ColorG { get; set; }

    [DataMember(Name = "colorB")]
    public float ColorB { get; set; }

    [DataMember(Name = "colorA")]
    public float ColorA { get; set; }
}

[DataContract]
public sealed class ModWorldRoadDto
{
    [DataMember(Name = "fromTile")]
    public int FromTile { get; set; }

    [DataMember(Name = "toTile")]
    public int ToTile { get; set; }

    [DataMember(Name = "roadDefName")]
    public string RoadDefName { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModWorldObjectBaselineDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "tileLayerId")]
    public int TileLayerId { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "factionDefName")]
    public string? FactionDefName { get; set; }
}

[DataContract]
public sealed class ModPlayerColonySiteDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "worldObjectId")]
    public string? WorldObjectId { get; set; }

    [DataMember(Name = "mapUniqueId")]
    public string? MapUniqueId { get; set; }

    [DataMember(Name = "tile")]
    public int Tile { get; set; }

    [DataMember(Name = "tileLayerId")]
    public int TileLayerId { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "factionName")]
    public string? FactionName { get; set; }

    [DataMember(Name = "appearance")]
    public ModColonyAppearanceDto? Appearance { get; set; }
}
