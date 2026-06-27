using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Plugins;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Network;

public sealed class WorldConfigurationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private readonly IBinaryPersistenceSlot? binaryPersistence;
    private readonly WorldConfigurationExtensionService worldExtensions;
    private string? administratorUserId;
    private HashSet<string> administratorUserIds = new(StringComparer.Ordinal);
    private WorldConfigurationDto? worldConfiguration;

    public WorldConfigurationRegistry(string? persistencePath = null)
        : this(
            string.IsNullOrWhiteSpace(persistencePath) ? null : new FileJsonPersistenceSlot(persistencePath),
            string.IsNullOrWhiteSpace(persistencePath) ? null : new FileBinaryPersistenceSlot(Path.ChangeExtension(persistencePath, null) + ".binary"))
    {
    }

    internal WorldConfigurationRegistry(IJsonPersistenceSlot? persistence)
        : this(persistence, persistence as IBinaryPersistenceSlot)
    {
    }

    internal WorldConfigurationRegistry(IJsonPersistenceSlot? persistence, IBinaryPersistenceSlot? binaryPersistence)
        : this(persistence, binaryPersistence, null)
    {
    }

    internal WorldConfigurationRegistry(
        IJsonPersistenceSlot? persistence,
        IBinaryPersistenceSlot? binaryPersistence,
        WorldConfigurationExtensionService? worldExtensions)
    {
        this.persistence = persistence;
        this.binaryPersistence = binaryPersistence;
        this.worldExtensions = worldExtensions ?? WorldConfigurationExtensionService.Empty;
        Load();
    }

    public WorldConfigurationRegistry(WorldConfigurationExtensionService? worldExtensions)
        : this(null, null, worldExtensions)
    {
    }

    public WorldConfigurationDto? Current
    {
        get
        {
            lock (gate)
            {
                return worldConfiguration is not null && HasUsableWorldGenerationBaseline(worldConfiguration)
                    ? worldConfiguration
                    : null;
            }
        }
    }

    public WorldSessionState Prepare(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            bool assignedAdministrator = false;
            if (administratorUserId is null)
            {
                administratorUserId = userId;
                administratorUserIds.Add(userId);
                assignedAdministrator = true;
            }

            if (assignedAdministrator)
            {
                SaveLocked();
            }

            bool isAdministrator = IsAdministratorLocked(userId);
            bool worldConfigured = worldConfiguration is not null && HasUsableWorldGenerationBaseline(worldConfiguration);
            return new WorldSessionState(
                isAdministrator,
                worldConfigured,
                RequiresInitialWorldConfiguration: isAdministrator && !worldConfigured,
                administratorUserId,
                worldConfigured ? worldConfiguration : null);
        }
    }

    public bool IsAdministrator(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return IsAdministratorLocked(userId);
        }
    }

    public IReadOnlyList<string> ListAdministrators()
    {
        lock (gate)
        {
            EnsurePrimaryAdministratorLocked();
            return administratorUserIds.OrderBy(userId => userId, StringComparer.Ordinal).ToList();
        }
    }

    public bool PromoteAdministrator(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            EnsurePrimaryAdministratorLocked();
            bool added = administratorUserIds.Add(userId);
            if (administratorUserId is null)
            {
                administratorUserId = userId;
                added = true;
            }

            if (added)
            {
                SaveLocked();
            }

            return added;
        }
    }

    public bool RevokeAdministrator(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            EnsurePrimaryAdministratorLocked();
            if (administratorUserIds.Count <= 1 || !administratorUserIds.Remove(userId))
            {
                return false;
            }

            if (string.Equals(administratorUserId, userId, StringComparison.Ordinal))
            {
                administratorUserId = administratorUserIds.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            }

            SaveLocked();
            return true;
        }
    }

    public WorldSessionState Submit(
        string userId,
        WorldConfigurationDto configuration,
        WorldConfigurationExtensionService? activeWorldExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(configuration);

        lock (gate)
        {
            administratorUserId ??= userId;
            administratorUserIds.Add(administratorUserId);
            bool isAdministrator = IsAdministratorLocked(userId);
            if (isAdministrator
                && HasUsableWorldGenerationBaseline(configuration))
            {
                worldConfiguration = MergeSubmittedWorldGenerationBaselineLocked(
                    EnsureSubmittedLanguageFeatureNameCatalog(configuration),
                    userId,
                    activeWorldExtensions);
                SaveLocked();
            }

            return new WorldSessionState(
                isAdministrator,
                worldConfiguration is not null && HasUsableWorldGenerationBaseline(worldConfiguration),
                RequiresInitialWorldConfiguration: false,
                administratorUserId,
                worldConfiguration is not null && HasUsableWorldGenerationBaseline(worldConfiguration)
                    ? worldConfiguration
                    : null);
        }
    }

    private WorldConfigurationDto MergeSubmittedWorldGenerationBaselineLocked(
        WorldConfigurationDto configuration,
        string userId,
        WorldConfigurationExtensionService? activeWorldExtensions)
    {
        if (worldConfiguration is null)
        {
            return configuration;
        }

        configuration = CopyWithFeatureNameCatalogs(
            configuration,
            MergeCompatibleFeatureNameCatalogs(worldConfiguration.FeatureNameCatalogs, configuration));

        IReadOnlyList<PlayerColonySiteDto> playerColonySites = MergePlayerColonySites(
            worldConfiguration.PlayerColonySites,
            configuration.PlayerColonySites);
        WorldConfigurationExtensionService extensionsService = ResolveWorldExtensions(activeWorldExtensions);
        IReadOnlyList<WorldConfigurationExtensionDto> extensions = extensionsService.MergeExtensions(
            new WorldConfigurationExtensionContext(
                userId,
                configuration.ConfiguredByColonyId,
                configuration.WorldConfigurationId),
            worldConfiguration.Extensions,
            configuration.Extensions);
        return CopyWithPlayerColonySitesAndExtensions(
            configuration,
            playerColonySites,
            extensions,
            worldConfiguration.GameLanguage);
    }

    public WorldFeatureNameCatalogSubmitResult SubmitWorldFeatureNames(
        string userId,
        string colonyId,
        string language,
        string worldConfigurationId,
        IReadOnlyList<WorldFeatureDto> features)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldConfigurationId);
        ArgumentNullException.ThrowIfNull(features);

        lock (gate)
        {
            if (worldConfiguration is null || !HasUsableWorldGenerationBaseline(worldConfiguration))
            {
                return new WorldFeatureNameCatalogSubmitResult(false, false, "WorldNotConfigured", worldConfiguration);
            }

            if (!string.Equals(worldConfiguration.WorldConfigurationId, worldConfigurationId, StringComparison.Ordinal))
            {
                return new WorldFeatureNameCatalogSubmitResult(false, false, "WorldConfigurationMismatch", worldConfiguration);
            }

            string normalizedLanguage = language.Trim();
            WorldFeatureNameCatalogDto? existing = FindFeatureNameCatalog(worldConfiguration, normalizedLanguage);
            if (existing is not null)
            {
                return new WorldFeatureNameCatalogSubmitResult(true, false, "AlreadyExists", worldConfiguration);
            }

            if (!IsWorldFeatureCatalogCompatible(worldConfiguration.Features, features, out string? failureReason))
            {
                return new WorldFeatureNameCatalogSubmitResult(false, false, failureReason ?? "FeatureCatalogMismatch", worldConfiguration);
            }

            WorldFeatureNameCatalogDto catalog = NormalizeFeatureNameCatalog(
                normalizedLanguage,
                worldConfiguration.WorldConfigurationId,
                worldConfiguration.Features,
                features);
            worldConfiguration = CopyWithFeatureNameCatalogs(
                worldConfiguration,
                worldConfiguration.FeatureNameCatalogs
                    .Concat(new[] { catalog })
                    .OrderBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            SaveLocked();
            return new WorldFeatureNameCatalogSubmitResult(true, true, "Created", worldConfiguration);
        }
    }

    public WorldSessionState RegisterPlayerColonySites(
        string userId,
        string colonyId,
        IReadOnlyList<PlayerColonySiteDto> sites,
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions = null,
        WorldConfigurationExtensionService? activeWorldExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentNullException.ThrowIfNull(sites);

        lock (gate)
        {
            administratorUserId ??= userId;
            administratorUserIds.Add(administratorUserId);
            bool isAdministrator = IsAdministratorLocked(userId);
            if (worldConfiguration is not null)
            {
                IReadOnlyList<PlayerColonySiteDto> mergedSites = worldConfiguration.PlayerColonySites;
                if (sites.Count > 0)
                {
                    var bySite = new Dictionary<string, PlayerColonySiteDto>(StringComparer.Ordinal);
                    foreach (PlayerColonySiteDto site in worldConfiguration.PlayerColonySites)
                    {
                        if (site.Tile >= 0
                            && !string.IsNullOrWhiteSpace(site.UserId)
                            && !string.IsNullOrWhiteSpace(site.ColonyId)
                            && (!string.Equals(site.UserId, userId, StringComparison.Ordinal)
                                || !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)))
                        {
                            bySite[ColonySiteKey(site)] = site;
                        }
                    }

                    foreach (PlayerColonySiteDto site in sites)
                    {
                        if (site.Tile >= 0)
                        {
                            PlayerColonySiteDto normalized = new PlayerColonySiteDto(
                                userId,
                                colonyId,
                                site.WorldObjectId,
                                site.MapUniqueId,
                                site.Tile,
                                site.Label,
                                site.FactionName,
                                site.Appearance,
                                site.TileLayerId);
                            bySite[ColonySiteKey(normalized)] = normalized;
                        }
                    }

                    mergedSites = bySite.Values
                        .OrderBy(site => site.Tile)
                        .ThenBy(site => site.UserId, StringComparer.Ordinal)
                        .ThenBy(site => site.ColonyId, StringComparer.Ordinal)
                        .ToList();
                }

                WorldConfigurationExtensionService extensionsService = ResolveWorldExtensions(activeWorldExtensions);
                worldConfiguration = CopyWithPlayerColonySitesAndExtensions(
                    worldConfiguration,
                    mergedSites,
                    extensionsService.MergeExtensions(
                        new WorldConfigurationExtensionContext(
                            userId,
                            colonyId,
                            worldConfiguration.WorldConfigurationId),
                        worldConfiguration.Extensions,
                        extensions ?? Array.Empty<WorldConfigurationExtensionDto>()));
                SaveLocked();
            }

            return new WorldSessionState(
                isAdministrator,
                worldConfiguration is not null,
                RequiresInitialWorldConfiguration: isAdministrator && worldConfiguration is null,
                administratorUserId,
                worldConfiguration);
        }
    }

    public RemovePlayerColonySitesResult RemovePlayerColonySites(
        string userId,
        string colonyId,
        WorldConfigurationExtensionService? activeWorldExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            bool isAdministrator = IsAdministratorLocked(userId);
            int removed = 0;
            if (worldConfiguration is not null)
            {
                List<PlayerColonySiteDto> retained = new();
                foreach (PlayerColonySiteDto site in worldConfiguration.PlayerColonySites)
                {
                    if (string.Equals(site.UserId, userId, StringComparison.Ordinal)
                        && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal))
                    {
                        removed++;
                        continue;
                    }

                    retained.Add(site);
                }

                if (removed > 0)
                {
                    WorldConfigurationExtensionService extensionsService = ResolveWorldExtensions(activeWorldExtensions);
                    worldConfiguration = CopyWithPlayerColonySitesAndExtensions(
                        worldConfiguration,
                        retained,
                        extensionsService.RemoveColonyExtensions(
                            userId,
                            colonyId,
                            worldConfiguration.Extensions));
                    SaveLocked();
                }
            }

            return new RemovePlayerColonySitesResult(
                removed,
                new WorldSessionState(
                    isAdministrator,
                    worldConfiguration is not null,
                    RequiresInitialWorldConfiguration: isAdministrator && worldConfiguration is null,
                    administratorUserId,
                    worldConfiguration));
        }
    }

    public IReadOnlyList<WorldTileFloatLayerIncrease> ConfirmWorldTileFloatLayer(
        string layerId,
        IReadOnlyList<WorldTileFloatLayerValue> values,
        WorldConfigurationExtensionService? activeWorldExtensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        ArgumentNullException.ThrowIfNull(values);

        lock (gate)
        {
            if (worldConfiguration is null)
            {
                return Array.Empty<WorldTileFloatLayerIncrease>();
            }

            WorldConfigurationExtensionService extensionsService = ResolveWorldExtensions(activeWorldExtensions);
            Dictionary<int, float> current = extensionsService
                .ReadTileFloatLayer(worldConfiguration.Extensions, layerId)
                .Where(tile => tile.Tile >= 0 && tile.Value > 0f)
                .GroupBy(tile => tile.Tile)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(tile => Math.Clamp(tile.Value, 0f, 1f)));
            List<WorldTileFloatLayerIncrease> increases = new();
            foreach (WorldTileFloatLayerValue tile in values)
            {
                if (tile.Tile < 0 || tile.Value <= 0f)
                {
                    continue;
                }

                float next = Math.Clamp(tile.Value, 0f, 1f);
                current.TryGetValue(tile.Tile, out float previous);
                if (next > previous + 0.0001f)
                {
                    current[tile.Tile] = next;
                    increases.Add(new WorldTileFloatLayerIncrease(layerId, tile.Tile, previous, next, next - previous));
                }
            }

            if (increases.Count > 0)
            {
                worldConfiguration = CopyWithTileFloatLayer(
                    worldConfiguration,
                    layerId,
                    current
                        .Where(pair => pair.Value > 0f)
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new WorldTileFloatLayerValue(pair.Key, pair.Value))
                        .ToList(),
                    extensionsService);
                SaveLocked();
            }

            return increases;
        }
    }

    private static string ColonySiteKey(PlayerColonySiteDto site)
    {
        string stableSiteId = !string.IsNullOrWhiteSpace(site.WorldObjectId)
            ? site.WorldObjectId!
            : !string.IsNullOrWhiteSpace(site.MapUniqueId)
                ? site.MapUniqueId!
                : "tile:" + site.Tile.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ","
                    + Math.Max(0, site.TileLayerId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return site.UserId + "\n" + site.ColonyId + "\n" + stableSiteId;
    }

    private static IReadOnlyList<PlayerColonySiteDto> MergePlayerColonySites(
        IReadOnlyList<PlayerColonySiteDto> current,
        IReadOnlyList<PlayerColonySiteDto>? incoming)
    {
        var bySite = new Dictionary<string, PlayerColonySiteDto>(StringComparer.Ordinal);
        foreach (PlayerColonySiteDto site in current)
        {
            if (site.Tile >= 0
                && !string.IsNullOrWhiteSpace(site.UserId)
                && !string.IsNullOrWhiteSpace(site.ColonyId))
            {
                bySite[ColonySiteKey(site)] = site;
            }
        }

        foreach (PlayerColonySiteDto site in incoming ?? Array.Empty<PlayerColonySiteDto>())
        {
            if (site.Tile >= 0
                && !string.IsNullOrWhiteSpace(site.UserId)
                && !string.IsNullOrWhiteSpace(site.ColonyId))
            {
                bySite[ColonySiteKey(site)] = site;
            }
        }

        return bySite.Values
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.UserId, StringComparer.Ordinal)
            .ThenBy(site => site.ColonyId, StringComparer.Ordinal)
            .ToList();
    }

    private static WorldConfigurationDto EnsureSubmittedLanguageFeatureNameCatalog(WorldConfigurationDto configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GameLanguage)
            || configuration.Features.Count == 0
            || FindFeatureNameCatalog(configuration, configuration.GameLanguage!) is not null)
        {
            return configuration;
        }

        WorldFeatureNameCatalogDto catalog = NormalizeFeatureNameCatalog(
            configuration.GameLanguage!,
            configuration.WorldConfigurationId,
            configuration.Features,
            configuration.Features);
        return CopyWithFeatureNameCatalogs(
            configuration,
            configuration.FeatureNameCatalogs
                .Concat(new[] { catalog })
                .OrderBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static IReadOnlyList<WorldFeatureNameCatalogDto> MergeCompatibleFeatureNameCatalogs(
        IReadOnlyList<WorldFeatureNameCatalogDto> existingCatalogs,
        WorldConfigurationDto submittedConfiguration)
    {
        var byLanguage = new Dictionary<string, WorldFeatureNameCatalogDto>(StringComparer.OrdinalIgnoreCase);
        foreach (WorldFeatureNameCatalogDto catalog in existingCatalogs)
        {
            if (string.IsNullOrWhiteSpace(catalog.Language)
                || !IsWorldFeatureCatalogCompatible(submittedConfiguration.Features, catalog.Features, out _))
            {
                continue;
            }

            byLanguage[catalog.Language.Trim()] = NormalizeFeatureNameCatalog(
                catalog.Language.Trim(),
                submittedConfiguration.WorldConfigurationId,
                submittedConfiguration.Features,
                catalog.Features);
        }

        foreach (WorldFeatureNameCatalogDto catalog in submittedConfiguration.FeatureNameCatalogs)
        {
            if (string.IsNullOrWhiteSpace(catalog.Language)
                || !IsWorldFeatureCatalogCompatible(submittedConfiguration.Features, catalog.Features, out _))
            {
                continue;
            }

            byLanguage[catalog.Language.Trim()] = NormalizeFeatureNameCatalog(
                catalog.Language.Trim(),
                submittedConfiguration.WorldConfigurationId,
                submittedConfiguration.Features,
                catalog.Features);
        }

        return byLanguage.Values
            .OrderBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorldFeatureNameCatalogDto? FindFeatureNameCatalog(
        WorldConfigurationDto configuration,
        string language)
    {
        return configuration.FeatureNameCatalogs.FirstOrDefault(catalog =>
            string.Equals(catalog.Language, language, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWorldFeatureCatalogCompatible(
        IReadOnlyList<WorldFeatureDto> baseline,
        IReadOnlyList<WorldFeatureDto> catalog,
        out string? failureReason)
    {
        if (baseline.Count != catalog.Count)
        {
            failureReason = "FeatureCountMismatch";
            return false;
        }

        for (int index = 0; index < baseline.Count; index++)
        {
            WorldFeatureDto expected = baseline[index];
            WorldFeatureDto actual = catalog[index];
            if (!string.Equals(expected.DefName, actual.DefName, StringComparison.Ordinal)
                || !NearlyEqual(expected.MaxDrawSizeInTiles, actual.MaxDrawSizeInTiles)
                || !NearlyEqual(expected.DrawCenterX, actual.DrawCenterX)
                || !NearlyEqual(expected.DrawCenterY, actual.DrawCenterY)
                || !NearlyEqual(expected.DrawCenterZ, actual.DrawCenterZ))
            {
                failureReason = "FeatureFingerprintMismatch";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private static bool NearlyEqual(float left, float right)
    {
        return Math.Abs(left - right) <= 0.0001f;
    }

    private static WorldFeatureNameCatalogDto NormalizeFeatureNameCatalog(
        string language,
        string worldConfigurationId,
        IReadOnlyList<WorldFeatureDto> baseline,
        IReadOnlyList<WorldFeatureDto> labels)
    {
        var features = new List<WorldFeatureDto>(baseline.Count);
        for (int index = 0; index < baseline.Count; index++)
        {
            WorldFeatureDto expected = baseline[index];
            string? label = index < labels.Count ? labels[index].Label : expected.Label;
            features.Add(new WorldFeatureDto(
                expected.DefName,
                string.IsNullOrWhiteSpace(label) ? expected.Label : label,
                expected.MaxDrawSizeInTiles,
                expected.DrawCenterX,
                expected.DrawCenterY,
                expected.DrawCenterZ));
        }

        return new WorldFeatureNameCatalogDto(language, worldConfigurationId, features);
    }

    private static WorldConfigurationDto CopyWithFeatureNameCatalogs(
        WorldConfigurationDto configuration,
        IReadOnlyList<WorldFeatureNameCatalogDto> featureNameCatalogs)
    {
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            configuration.SeedString,
            configuration.PlanetCoverage,
            configuration.OverallRainfall,
            configuration.OverallTemperature,
            configuration.OverallPopulation,
            configuration.LandmarkDensity,
            configuration.TileCount,
            configuration.FactionDefNames,
            configuration.Features,
            configuration.Factions,
            configuration.Roads,
            configuration.WorldObjects,
            configuration.PlayerColonySites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            configuration.TileGeometry,
            configuration.DifficultyValuesXml,
            configuration.Extensions,
            configuration.GameLanguage,
            featureNameCatalogs);
    }

    private static WorldConfigurationDto CopyWithPlayerColonySitesAndExtensions(
        WorldConfigurationDto configuration,
        IReadOnlyList<PlayerColonySiteDto> playerColonySites,
        IReadOnlyList<WorldConfigurationExtensionDto> extensions,
        string? gameLanguage = null)
    {
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            configuration.SeedString,
            configuration.PlanetCoverage,
            configuration.OverallRainfall,
            configuration.OverallTemperature,
            configuration.OverallPopulation,
            configuration.LandmarkDensity,
            configuration.TileCount,
            configuration.FactionDefNames,
            configuration.Features,
            configuration.Factions,
            configuration.Roads,
            configuration.WorldObjects,
            playerColonySites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            configuration.TileGeometry,
            configuration.DifficultyValuesXml,
            extensions,
            gameLanguage ?? configuration.GameLanguage,
            configuration.FeatureNameCatalogs);
    }

    private WorldConfigurationDto CopyWithTileFloatLayer(
        WorldConfigurationDto configuration,
        string layerId,
        IReadOnlyList<WorldTileFloatLayerValue> values,
        WorldConfigurationExtensionService activeWorldExtensions)
    {
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            configuration.SeedString,
            configuration.PlanetCoverage,
            configuration.OverallRainfall,
            configuration.OverallTemperature,
            configuration.OverallPopulation,
            configuration.LandmarkDensity,
            configuration.TileCount,
            configuration.FactionDefNames,
            configuration.Features,
            configuration.Factions,
            configuration.Roads,
            configuration.WorldObjects,
            configuration.PlayerColonySites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            configuration.TileGeometry,
            configuration.DifficultyValuesXml,
            activeWorldExtensions.ReplaceTileFloatLayer(
                configuration.Extensions,
                layerId,
                values),
            configuration.GameLanguage,
            configuration.FeatureNameCatalogs);
    }

    private WorldConfigurationExtensionService ResolveWorldExtensions(WorldConfigurationExtensionService? activeWorldExtensions)
    {
        return activeWorldExtensions ?? worldExtensions;
    }

    private void Load()
    {
        if (persistence is null)
        {
            return;
        }

        try
        {
            string? json = persistence.Read();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            WorldConfigurationPersistence? persisted =
                JsonSerializer.Deserialize<WorldConfigurationPersistence>(json, JsonOptions);
            administratorUserId = string.IsNullOrWhiteSpace(persisted?.AdministratorUserId)
                ? null
                : persisted!.AdministratorUserId;
            administratorUserIds = persisted?.AdministratorUserIds is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : persisted.AdministratorUserIds
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .ToHashSet(StringComparer.Ordinal);
            EnsurePrimaryAdministratorLocked();
            worldConfiguration = RestorePersistedWorldConfiguration(persisted);
        }
        catch (JsonException)
        {
            administratorUserId = null;
            worldConfiguration = null;
        }
        catch (IOException)
        {
            administratorUserId = null;
            worldConfiguration = null;
        }
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            BuildPersistenceSnapshot(administratorUserId, administratorUserIds, worldConfiguration),
            JsonOptions);
        persistence.Write(json);
        binaryPersistence?.WriteBinary("tile-geometry", WorldTileGeometryBinaryCodec.Encode(worldConfiguration?.TileGeometry));
    }

    private bool IsAdministratorLocked(string userId)
    {
        EnsurePrimaryAdministratorLocked();
        return administratorUserIds.Contains(userId);
    }

    private void EnsurePrimaryAdministratorLocked()
    {
        if (!string.IsNullOrWhiteSpace(administratorUserId))
        {
            administratorUserIds.Add(administratorUserId);
        }
        else if (administratorUserIds.Count > 0)
        {
            administratorUserId = administratorUserIds.OrderBy(userId => userId, StringComparer.Ordinal).First();
        }
    }

    private static bool HasUsableWorldGenerationBaseline(WorldConfigurationDto configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.SeedString)
            && float.TryParse(
                configuration.PlanetCoverage,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float coverage)
            && coverage > 0f
            && configuration.FactionDefNames.Count > 0;
    }

    private static WorldConfigurationPersistence BuildPersistenceSnapshot(
        string? administratorUserId,
        IReadOnlyCollection<string> administratorUserIds,
        WorldConfigurationDto? configuration)
    {
        WorldConfigurationDto? persistedConfiguration = configuration is null
            ? null
            : CopyWithTileGeometry(configuration, tileGeometry: null);
        return new WorldConfigurationPersistence(
            administratorUserId,
            administratorUserIds.ToList(),
            persistedConfiguration);
    }

    private WorldConfigurationDto? RestorePersistedWorldConfiguration(WorldConfigurationPersistence? persisted)
    {
        WorldConfigurationDto? configuration = persisted?.WorldConfiguration;
        if (configuration is null)
        {
            return null;
        }

        WorldTileGeometryDto? restoredGeometry = WorldTileGeometryBinaryCodec.Decode(binaryPersistence?.ReadBinary("tile-geometry"));
        return restoredGeometry is null
            ? configuration
            : CopyWithTileGeometry(configuration, restoredGeometry);
    }

    private static WorldConfigurationDto CopyWithTileGeometry(
        WorldConfigurationDto configuration,
        WorldTileGeometryDto? tileGeometry)
    {
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            configuration.SeedString,
            configuration.PlanetCoverage,
            configuration.OverallRainfall,
            configuration.OverallTemperature,
            configuration.OverallPopulation,
            configuration.LandmarkDensity,
            configuration.TileCount,
            configuration.FactionDefNames,
            configuration.Features,
            configuration.Factions,
            configuration.Roads,
            configuration.WorldObjects,
            configuration.PlayerColonySites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            tileGeometry,
            configuration.DifficultyValuesXml,
            configuration.Extensions,
            configuration.GameLanguage,
            configuration.FeatureNameCatalogs);
    }

    private sealed record WorldConfigurationPersistence(
        string? AdministratorUserId,
        IReadOnlyList<string>? AdministratorUserIds,
        WorldConfigurationDto? WorldConfiguration);
}

internal static class WorldTileGeometryBinaryCodec
{
    private const uint Magic = 0x524F4354; // TCOR
    private const int Version = 1;
    private const int HeaderSize = 12;
    private const int LayerHeaderSize = 16;
    private const int TileCenterSize = 16;

    public static byte[]? Encode(WorldTileGeometryDto? geometry)
    {
        if (geometry is null || geometry.Layers.Count == 0)
        {
            return null;
        }

        int length = HeaderSize;
        var layerNames = new List<byte[]>(geometry.Layers.Count);
        foreach (WorldTileLayerGeometryDto layer in geometry.Layers)
        {
            byte[] layerName = string.IsNullOrWhiteSpace(layer.LayerDefName)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(layer.LayerDefName!);
            layerNames.Add(layerName);
            checked
            {
                length += LayerHeaderSize + layerName.Length + layer.TileCenters.Count * TileCenterSize;
            }
        }

        byte[] bytes = new byte[length];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..12], geometry.Layers.Count);
        int offset = HeaderSize;

        for (int layerIndex = 0; layerIndex < geometry.Layers.Count; layerIndex++)
        {
            WorldTileLayerGeometryDto layer = geometry.Layers[layerIndex];
            byte[] layerName = layerNames[layerIndex];
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), layer.LayerId);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), layer.AverageTileSize);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), layer.TileCenters.Count);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 12, 4), layerName.Length);
            offset += LayerHeaderSize;
            layerName.CopyTo(span.Slice(offset, layerName.Length));
            offset += layerName.Length;

            foreach (WorldTileCenterDto center in layer.TileCenters.OrderBy(center => center.Tile))
            {
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), center.Tile);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), center.X);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), center.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12, 4), center.Z);
                offset += TileCenterSize;
            }
        }

        return bytes;
    }

    public static WorldTileGeometryDto? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < HeaderSize)
        {
            return null;
        }

        ReadOnlySpan<byte> span = bytes;
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(span[4..8]) != Version)
        {
            return null;
        }

        int layerCount = BinaryPrimitives.ReadInt32LittleEndian(span[8..12]);
        if (layerCount <= 0)
        {
            return null;
        }

        int offset = HeaderSize;
        var layers = new List<WorldTileLayerGeometryDto>(layerCount);
        for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            if (offset + LayerHeaderSize > bytes.Length)
            {
                return null;
            }

            int layerId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            float averageTileSize = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
            int tileCenterCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 8, 4));
            int layerNameByteCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 12, 4));
            offset += LayerHeaderSize;
            if (tileCenterCount < 0
                || layerNameByteCount < 0
                || offset + layerNameByteCount > bytes.Length
                || offset + layerNameByteCount + tileCenterCount * TileCenterSize > bytes.Length)
            {
                return null;
            }

            string? layerDefName = layerNameByteCount == 0
                ? null
                : Encoding.UTF8.GetString(bytes, offset, layerNameByteCount);
            offset += layerNameByteCount;
            var tileCenters = new List<WorldTileCenterDto>(tileCenterCount);
            for (int tileIndex = 0; tileIndex < tileCenterCount; tileIndex++)
            {
                int tile = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                float x = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4));
                float z = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 12, 4));
                tileCenters.Add(new WorldTileCenterDto(tile, x, y, z));
                offset += TileCenterSize;
            }

            layers.Add(new WorldTileLayerGeometryDto(layerId, layerDefName, averageTileSize, tileCenters));
        }

        return new WorldTileGeometryDto(layers);
    }
}

public sealed record WorldSessionState(
    bool IsAdministrator,
    bool WorldConfigured,
    bool RequiresInitialWorldConfiguration,
    string? AdministratorUserId,
    WorldConfigurationDto? WorldConfiguration);

public sealed record RemovePlayerColonySitesResult(int RemovedCount, WorldSessionState Session);

public sealed record WorldFeatureNameCatalogSubmitResult(
    bool Accepted,
    bool Created,
    string Message,
    WorldConfigurationDto? WorldConfiguration);
