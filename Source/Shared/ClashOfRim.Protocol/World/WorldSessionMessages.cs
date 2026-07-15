using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class PrepareWorldSessionRequest
{
    public PrepareWorldSessionRequest(
        string protocolVersion,
        string userId,
        string? colonyId = null,
        string? compatibilityManifestJson = null,
        string? steamAuthTicket = null,
        string? password = null,
        string? compatibilityManifestId = null,
        string? compatibilityManifestSummaryJson = null,
        bool createAccountIfMissing = false)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        CompatibilityManifestJson = compatibilityManifestJson;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
        CompatibilityManifestId = compatibilityManifestId;
        CompatibilityManifestSummaryJson = compatibilityManifestSummaryJson;
        CreateAccountIfMissing = createAccountIfMissing;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string? ColonyId { get; }

    public string? CompatibilityManifestJson { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }

    public string? CompatibilityManifestId { get; }

    public string? CompatibilityManifestSummaryJson { get; }

    public bool CreateAccountIfMissing { get; }
}

public sealed class PrepareWorldSessionResponse
{
    public PrepareWorldSessionResponse(
        ProtocolResponse result,
        bool isAdministrator,
        bool worldConfigured,
        bool requiresInitialWorldConfiguration,
        string? administratorUserId,
        WorldConfigurationDto? worldConfiguration,
        bool hasExistingColony = false,
        string? latestSnapshotId = null,
        string? serverCompatibilityManifestJson = null,
        IReadOnlyList<CompatibilityIssueDto>? compatibilityIssues = null,
        bool canOverrideCompatibilityBaseline = false,
        string? assignedColonyId = null,
        bool requiresFullCompatibilityManifest = false,
        IReadOnlyList<string>? requestedCompatibilityPackageIds = null)
    {
        Result = result;
        IsAdministrator = isAdministrator;
        WorldConfigured = worldConfigured;
        RequiresInitialWorldConfiguration = requiresInitialWorldConfiguration;
        AdministratorUserId = administratorUserId;
        WorldConfiguration = worldConfiguration;
        HasExistingColony = hasExistingColony;
        LatestSnapshotId = latestSnapshotId;
        ServerCompatibilityManifestJson = serverCompatibilityManifestJson;
        CompatibilityIssues = compatibilityIssues ?? Array.Empty<CompatibilityIssueDto>();
        CanOverrideCompatibilityBaseline = canOverrideCompatibilityBaseline;
        AssignedColonyId = assignedColonyId;
        RequiresFullCompatibilityManifest = requiresFullCompatibilityManifest;
        RequestedCompatibilityPackageIds = requestedCompatibilityPackageIds ?? Array.Empty<string>();
    }

    public ProtocolResponse Result { get; }

    public bool IsAdministrator { get; }

    public bool WorldConfigured { get; }

    public bool RequiresInitialWorldConfiguration { get; }

    public string? AdministratorUserId { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }

    public bool HasExistingColony { get; }

    public string? LatestSnapshotId { get; }

    public string? ServerCompatibilityManifestJson { get; }

    public IReadOnlyList<CompatibilityIssueDto> CompatibilityIssues { get; }

    public bool CanOverrideCompatibilityBaseline { get; }

    public string? AssignedColonyId { get; }

    public bool RequiresFullCompatibilityManifest { get; }

    public IReadOnlyList<string> RequestedCompatibilityPackageIds { get; }
}

public sealed class SubmitWorldConfigurationRequest
{
    public SubmitWorldConfigurationRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        WorldConfigurationDto configuration)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        Configuration = configuration;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public WorldConfigurationDto Configuration { get; }
}

public sealed class GetWorldConfigurationRequest
{
    public GetWorldConfigurationRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string? steamAuthTicket = null,
        string? password = null,
        bool includeGenerationBaseline = true,
        bool includePlayerColonySites = true,
        bool includeWorldExtensions = true)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
        IncludeGenerationBaseline = includeGenerationBaseline;
        IncludePlayerColonySites = includePlayerColonySites;
        IncludeWorldExtensions = includeWorldExtensions;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }

    public bool IncludeGenerationBaseline { get; }

    public bool IncludePlayerColonySites { get; }

    public bool IncludeWorldExtensions { get; }
}

public sealed class GetWorldConfigurationResponse
{
    public GetWorldConfigurationResponse(
        ProtocolResponse result,
        WorldConfigurationDto? worldConfiguration)
    {
        Result = result;
        WorldConfiguration = worldConfiguration;
    }

    public ProtocolResponse Result { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }
}

public sealed class SubmitWorldFeatureNamesRequest
{
    public SubmitWorldFeatureNamesRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string language,
        string worldConfigurationId,
        IReadOnlyList<WorldFeatureDto>? features,
        string? steamAuthTicket = null,
        string? password = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        Language = language;
        WorldConfigurationId = worldConfigurationId;
        Features = features ?? Array.Empty<WorldFeatureDto>();
        SteamAuthTicket = steamAuthTicket;
        Password = password;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string Language { get; }

    public string WorldConfigurationId { get; }

    public IReadOnlyList<WorldFeatureDto> Features { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }
}

public sealed class UploadWorldSubstrateRequest
{
    public UploadWorldSubstrateRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string worldConfigurationId,
        string? steamAuthTicket = null,
        string? password = null,
        string? authToken = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        WorldConfigurationId = worldConfigurationId;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
        AuthToken = authToken;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string WorldConfigurationId { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }

    public string? AuthToken { get; }
}

public sealed class UploadWorldSubstrateResponse
{
    public UploadWorldSubstrateResponse(
        ProtocolResponse result,
        bool accepted,
        int layerCount,
        int tileCenterCount,
        WorldConfigurationDto? worldConfiguration)
    {
        Result = result;
        Accepted = accepted;
        LayerCount = layerCount;
        TileCenterCount = tileCenterCount;
        WorldConfiguration = worldConfiguration;
    }

    public ProtocolResponse Result { get; }

    public bool Accepted { get; }

    public int LayerCount { get; }

    public int TileCenterCount { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }
}

public sealed class DownloadWorldSubstrateRequest
{
    public DownloadWorldSubstrateRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string worldConfigurationId,
        string? steamAuthTicket = null,
        string? password = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        WorldConfigurationId = worldConfigurationId;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string WorldConfigurationId { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }
}

public sealed class SubmitWorldFeatureNamesResponse
{
    public SubmitWorldFeatureNamesResponse(
        ProtocolResponse result,
        bool accepted,
        bool created,
        WorldConfigurationDto? worldConfiguration)
    {
        Result = result;
        Accepted = accepted;
        Created = created;
        WorldConfiguration = worldConfiguration;
    }

    public ProtocolResponse Result { get; }

    public bool Accepted { get; }

    public bool Created { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }
}

public sealed class SubmitWorldConfigurationResponse
{
    public SubmitWorldConfigurationResponse(
        ProtocolResponse result,
        bool isAdministrator,
        bool worldConfigured,
        string? administratorUserId,
        WorldConfigurationDto? worldConfiguration)
    {
        Result = result;
        IsAdministrator = isAdministrator;
        WorldConfigured = worldConfigured;
        AdministratorUserId = administratorUserId;
        WorldConfiguration = worldConfiguration;
    }

    public ProtocolResponse Result { get; }

    public bool IsAdministrator { get; }

    public bool WorldConfigured { get; }

    public string? AdministratorUserId { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }
}

public sealed class RegisterPlayerColonySitesRequest
{
    public RegisterPlayerColonySitesRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        IReadOnlyList<PlayerColonySiteDto>? sites,
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions = null,
        bool suppressWorldConfigurationNotification = false)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        Sites = sites ?? Array.Empty<PlayerColonySiteDto>();
        Extensions = extensions ?? Array.Empty<WorldConfigurationExtensionDto>();
        SuppressWorldConfigurationNotification = suppressWorldConfigurationNotification;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public IReadOnlyList<PlayerColonySiteDto> Sites { get; }

    public IReadOnlyList<WorldConfigurationExtensionDto> Extensions { get; }

    public bool SuppressWorldConfigurationNotification { get; }
}

public sealed class RegisterPlayerColonySitesResponse
{
    public RegisterPlayerColonySitesResponse(
        ProtocolResponse result,
        int acceptedCount,
        WorldConfigurationDto? worldConfiguration,
        WorldMapMarkerDeliveryDto? worldMapMarkers)
    {
        Result = result;
        AcceptedCount = acceptedCount;
        WorldConfiguration = worldConfiguration;
        WorldMapMarkers = worldMapMarkers;
    }

    public ProtocolResponse Result { get; }

    public int AcceptedCount { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }

    public WorldMapMarkerDeliveryDto? WorldMapMarkers { get; }
}

public sealed class PreflightColonyRelocationRequest
{
    public PreflightColonyRelocationRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string currentSnapshotId,
        int targetTile,
        string idempotencyKey,
        string? authToken = null,
        int targetTileLayerId = 0)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        TargetTile = targetTile;
        TargetTileLayerId = Math.Max(0, targetTileLayerId);
        IdempotencyKey = idempotencyKey;
        AuthToken = authToken;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public int TargetTile { get; }

    public int TargetTileLayerId { get; }

    public string IdempotencyKey { get; }

    public string? AuthToken { get; }
}

public sealed class ConfirmColonyRelocationRequest
{
    public ConfirmColonyRelocationRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string previousSnapshotId,
        string relocatedSnapshotId,
        int targetTile,
        string idempotencyKey,
        string? authToken = null,
        int targetTileLayerId = 0)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        PreviousSnapshotId = previousSnapshotId;
        RelocatedSnapshotId = relocatedSnapshotId;
        TargetTile = targetTile;
        TargetTileLayerId = Math.Max(0, targetTileLayerId);
        IdempotencyKey = idempotencyKey;
        AuthToken = authToken;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string PreviousSnapshotId { get; }

    public string RelocatedSnapshotId { get; }

    public int TargetTile { get; }

    public int TargetTileLayerId { get; }

    public string IdempotencyKey { get; }

    public string? AuthToken { get; }
}

public sealed class ColonyRelocationResponse
{
    public ColonyRelocationResponse(
        ProtocolResponse result,
        PlayerColonySiteDto? oldSite,
        PlayerColonySiteDto? newSite,
        WorldConfigurationDto? worldConfiguration,
        WorldMapMarkerDeliveryDto? worldMapMarkers)
    {
        Result = result;
        OldSite = oldSite;
        NewSite = newSite;
        WorldConfiguration = worldConfiguration;
        WorldMapMarkers = worldMapMarkers;
    }

    public ProtocolResponse Result { get; }

    public PlayerColonySiteDto? OldSite { get; }

    public PlayerColonySiteDto? NewSite { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }

    public WorldMapMarkerDeliveryDto? WorldMapMarkers { get; }
}

public sealed class AbandonPlayerColonyRequest
{
    public AbandonPlayerColonyRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string? currentSnapshotId,
        string idempotencyKey,
        string? authToken = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        IdempotencyKey = idempotencyKey;
        AuthToken = authToken;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public string IdempotencyKey { get; }

    public string? AuthToken { get; }
}

public sealed class AbandonPlayerColonyResponse
{
    public AbandonPlayerColonyResponse(
        ProtocolResponse result,
        int removedSnapshots,
        int removedSites,
        int removedEvents,
        int removedRuntimeMarkers,
        WorldConfigurationDto? worldConfiguration,
        WorldMapMarkerDeliveryDto? worldMapMarkers)
    {
        Result = result;
        RemovedSnapshots = removedSnapshots;
        RemovedSites = removedSites;
        RemovedEvents = removedEvents;
        RemovedRuntimeMarkers = removedRuntimeMarkers;
        WorldConfiguration = worldConfiguration;
        WorldMapMarkers = worldMapMarkers;
    }

    public ProtocolResponse Result { get; }

    public int RemovedSnapshots { get; }

    public int RemovedSites { get; }

    public int RemovedEvents { get; }

    public int RemovedRuntimeMarkers { get; }

    public WorldConfigurationDto? WorldConfiguration { get; }

    public WorldMapMarkerDeliveryDto? WorldMapMarkers { get; }
}

public sealed class WorldConfigurationDto
{
    public WorldConfigurationDto(
        string worldConfigurationId,
        string configuredByUserId,
        string configuredByColonyId,
        DateTimeOffset configuredAtUtc,
        string? seedString,
        string? planetCoverage,
        string? overallRainfall,
        string? overallTemperature,
        string? overallPopulation,
        string? landmarkDensity,
        string? tileCount,
        IReadOnlyList<string>? factionDefNames,
        IReadOnlyList<WorldFeatureDto>? features,
        IReadOnlyList<WorldFactionDto>? factions,
        IReadOnlyList<WorldRoadDto>? roads,
        IReadOnlyList<WorldObjectBaselineDto>? worldObjects,
        IReadOnlyList<PlayerColonySiteDto>? playerColonySites,
        string? storytellerDefName = null,
        string? difficultyDefName = null,
        WorldTileGeometryDto? tileGeometry = null,
        string? difficultyValuesXml = null,
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions = null,
        string? gameLanguage = null,
        IReadOnlyList<WorldFeatureNameCatalogDto>? featureNameCatalogs = null)
    {
        WorldConfigurationId = worldConfigurationId;
        ConfiguredByUserId = configuredByUserId;
        ConfiguredByColonyId = configuredByColonyId;
        ConfiguredAtUtc = configuredAtUtc;
        SeedString = seedString;
        PlanetCoverage = planetCoverage;
        OverallRainfall = overallRainfall;
        OverallTemperature = overallTemperature;
        OverallPopulation = overallPopulation;
        LandmarkDensity = landmarkDensity;
        TileCount = tileCount;
        FactionDefNames = factionDefNames ?? Array.Empty<string>();
        Features = features ?? Array.Empty<WorldFeatureDto>();
        Factions = factions ?? Array.Empty<WorldFactionDto>();
        Roads = roads ?? Array.Empty<WorldRoadDto>();
        WorldObjects = worldObjects ?? Array.Empty<WorldObjectBaselineDto>();
        PlayerColonySites = playerColonySites ?? Array.Empty<PlayerColonySiteDto>();
        StorytellerDefName = storytellerDefName;
        DifficultyDefName = difficultyDefName;
        TileGeometry = tileGeometry;
        DifficultyValuesXml = difficultyValuesXml;
        Extensions = extensions ?? Array.Empty<WorldConfigurationExtensionDto>();
        GameLanguage = gameLanguage;
        FeatureNameCatalogs = featureNameCatalogs ?? Array.Empty<WorldFeatureNameCatalogDto>();
    }

    public string WorldConfigurationId { get; }

    public string ConfiguredByUserId { get; }

    public string ConfiguredByColonyId { get; }

    public DateTimeOffset ConfiguredAtUtc { get; }

    public string? SeedString { get; }

    public string? PlanetCoverage { get; }

    public string? OverallRainfall { get; }

    public string? OverallTemperature { get; }

    public string? OverallPopulation { get; }

    public string? LandmarkDensity { get; }

    public string? TileCount { get; }

    public IReadOnlyList<string> FactionDefNames { get; }

    public IReadOnlyList<WorldFeatureDto> Features { get; }

    public IReadOnlyList<WorldFactionDto> Factions { get; }

    public IReadOnlyList<WorldRoadDto> Roads { get; }

    public IReadOnlyList<WorldObjectBaselineDto> WorldObjects { get; }

    public IReadOnlyList<PlayerColonySiteDto> PlayerColonySites { get; }

    public string? StorytellerDefName { get; }

    public string? DifficultyDefName { get; }

    public WorldTileGeometryDto? TileGeometry { get; }

    public string? DifficultyValuesXml { get; }

    public IReadOnlyList<WorldConfigurationExtensionDto> Extensions { get; }

    public string? GameLanguage { get; }

    public IReadOnlyList<WorldFeatureNameCatalogDto> FeatureNameCatalogs { get; }
}

public sealed class WorldFeatureNameCatalogDto
{
    public WorldFeatureNameCatalogDto(
        string language,
        string? worldConfigurationId,
        IReadOnlyList<WorldFeatureDto>? features)
    {
        Language = language;
        WorldConfigurationId = worldConfigurationId;
        Features = features ?? Array.Empty<WorldFeatureDto>();
    }

    public string Language { get; }

    public string? WorldConfigurationId { get; }

    public IReadOnlyList<WorldFeatureDto> Features { get; }
}

public sealed class WorldConfigurationExtensionDto
{
    public WorldConfigurationExtensionDto(
        string providerId,
        string kind,
        string schemaVersion,
        string? payloadJson,
        IReadOnlyDictionary<string, string?>? metadata = null)
    {
        ProviderId = providerId;
        Kind = kind;
        SchemaVersion = schemaVersion;
        PayloadJson = payloadJson;
        Metadata = metadata ?? new Dictionary<string, string?>();
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public string SchemaVersion { get; }

    public string? PayloadJson { get; }

    public IReadOnlyDictionary<string, string?> Metadata { get; }
}

public sealed class WorldTileGeometryDto
{
    public WorldTileGeometryDto(IReadOnlyList<WorldTileLayerGeometryDto>? layers)
    {
        Layers = layers ?? Array.Empty<WorldTileLayerGeometryDto>();
    }

    public IReadOnlyList<WorldTileLayerGeometryDto> Layers { get; }
}

public sealed class WorldTileLayerGeometryDto
{
    public WorldTileLayerGeometryDto(
        int layerId,
        string? layerDefName,
        float averageTileSize,
        IReadOnlyList<WorldTileCenterDto>? tileCenters)
    {
        LayerId = layerId;
        LayerDefName = layerDefName;
        AverageTileSize = averageTileSize;
        TileCenters = tileCenters ?? Array.Empty<WorldTileCenterDto>();
    }

    public int LayerId { get; }

    public string? LayerDefName { get; }

    public float AverageTileSize { get; }

    public IReadOnlyList<WorldTileCenterDto> TileCenters { get; }
}

public sealed class WorldTileCenterDto
{
    public WorldTileCenterDto(int tile, float x, float y, float z)
    {
        Tile = tile;
        X = x;
        Y = y;
        Z = z;
    }

    public int Tile { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }
}

public sealed class WorldFeatureDto
{
    public WorldFeatureDto(string defName, string? label, float maxDrawSizeInTiles, float drawCenterX, float drawCenterY, float drawCenterZ)
    {
        DefName = defName;
        Label = label;
        MaxDrawSizeInTiles = maxDrawSizeInTiles;
        DrawCenterX = drawCenterX;
        DrawCenterY = drawCenterY;
        DrawCenterZ = drawCenterZ;
    }

    public string DefName { get; }

    public string? Label { get; }

    public float MaxDrawSizeInTiles { get; }

    public float DrawCenterX { get; }

    public float DrawCenterY { get; }

    public float DrawCenterZ { get; }
}

public sealed class WorldFactionDto
{
    public WorldFactionDto(string defName, string? name, float colorR, float colorG, float colorB, float colorA)
    {
        DefName = defName;
        Name = name;
        ColorR = colorR;
        ColorG = colorG;
        ColorB = colorB;
        ColorA = colorA;
    }

    public string DefName { get; }

    public string? Name { get; }

    public float ColorR { get; }

    public float ColorG { get; }

    public float ColorB { get; }

    public float ColorA { get; }
}

public sealed class WorldRoadDto
{
    public WorldRoadDto(int fromTile, int toTile, string roadDefName)
    {
        FromTile = fromTile;
        ToTile = toTile;
        RoadDefName = roadDefName;
    }

    public int FromTile { get; }

    public int ToTile { get; }

    public string RoadDefName { get; }
}

public sealed class WorldObjectBaselineDto
{
    public WorldObjectBaselineDto(string defName, int tile, string? label, string? factionDefName)
    {
        DefName = defName;
        Tile = tile;
        Label = label;
        FactionDefName = factionDefName;
    }

    public string DefName { get; }

    public int Tile { get; }

    public string? Label { get; }

    public string? FactionDefName { get; }
}

public sealed class PlayerColonySiteDto
{
    public PlayerColonySiteDto(
        string userId,
        string colonyId,
        string? worldObjectId,
        string? mapUniqueId,
        int tile,
        string? label,
        string? factionName = null,
        ColonyAppearanceDto? appearance = null,
        int tileLayerId = 0)
    {
        UserId = userId;
        ColonyId = colonyId;
        WorldObjectId = worldObjectId;
        MapUniqueId = mapUniqueId;
        Tile = tile;
        Label = label;
        FactionName = factionName;
        Appearance = appearance;
        TileLayerId = Math.Max(0, tileLayerId);
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? WorldObjectId { get; }

    public string? MapUniqueId { get; }

    public int Tile { get; }

    public int TileLayerId { get; }

    public string? Label { get; }

    public string? FactionName { get; }

    public ColonyAppearanceDto? Appearance { get; }
}

public sealed class ColonyAppearanceDto
{
    public ColonyAppearanceDto(
        string? mode = null,
        string? iconDefName = null,
        string? colorDefName = null,
        string? colorHex = null)
    {
        Mode = mode;
        IconDefName = iconDefName;
        ColorDefName = colorDefName;
        ColorHex = colorHex;
    }

    public string? Mode { get; }

    public string? IconDefName { get; }

    public string? ColorDefName { get; }

    public string? ColorHex { get; }
}
