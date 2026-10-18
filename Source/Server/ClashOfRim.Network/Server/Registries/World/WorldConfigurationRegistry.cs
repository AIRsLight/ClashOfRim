using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
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
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly IBinaryPersistenceSlot? binaryPersistence;
    private readonly WorldConfigurationExtensionService worldExtensions;
    private string? administratorUserId;
    private HashSet<string> administratorUserIds = new(StringComparer.Ordinal);
    private WorldConfigurationDto? worldConfiguration;
    private byte[]? worldSubstratePayload;
    private string? worldSubstrateKey = string.Empty;
    private IReadOnlyList<WorldLayerNotificationWork> pendingLayerNotifications = [];

    internal bool HasPendingLayerNotifications { get { lock (gate) return pendingLayerNotifications.Count > 0; } }
    internal IReadOnlyList<WorldLayerNotificationWork> ListPendingLayerNotifications()
    { lock (gate) return pendingLayerNotifications.ToArray(); }

    internal void CompleteLayerNotifications(string id)
    {
        lock (gate)
        {
            var remaining = pendingLayerNotifications.Where(work => work.Id != id).ToArray();
            if (remaining.Length == pendingLayerNotifications.Count) return;
            SaveLocked(worldConfiguration, writeSubstrate: false, remaining);
            pendingLayerNotifications = remaining;
        }
    }

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
        : this(null, persistence, binaryPersistence, worldExtensions)
    {
    }

    internal WorldConfigurationRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence,
        IBinaryPersistenceSlot? binaryPersistence,
        WorldConfigurationExtensionService? worldExtensions)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
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
                return HasReadyWorldLocked()
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
            if (administratorUserId is null)
                PersistAdministratorsLocked(userId, new HashSet<string>(administratorUserIds, StringComparer.Ordinal) { userId });

            return SessionStateLocked(userId);
        }
    }

    public WorldSessionState GetSessionState(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (gate) return SessionStateLocked(userId);
    }

    private WorldSessionState SessionStateLocked(string userId)
    {
        bool isAdministrator = IsAdministratorLocked(userId);
        bool worldConfigured = HasReadyWorldLocked();
        return new WorldSessionState(isAdministrator, worldConfigured,
            RequiresInitialWorldConfiguration: isAdministrator && !worldConfigured,
            administratorUserId, worldConfigured ? worldConfiguration : null);
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
            var candidate = new HashSet<string>(administratorUserIds, StringComparer.Ordinal);
            bool added = candidate.Add(userId) || administratorUserId is null;
            if (added) PersistAdministratorsLocked(administratorUserId ?? userId, candidate);
            return added;
        }
    }

    public bool RevokeAdministrator(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            EnsurePrimaryAdministratorLocked();
            if (administratorUserIds.Count <= 1 || !administratorUserIds.Contains(userId)) return false;
            var candidate = new HashSet<string>(administratorUserIds, StringComparer.Ordinal);
            candidate.Remove(userId);
            string? primary = administratorUserId == userId ? candidate.OrderBy(id => id, StringComparer.Ordinal).First() : administratorUserId;
            PersistAdministratorsLocked(primary, candidate);
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
            string primary = administratorUserId ?? userId;
            var administrators = new HashSet<string>(administratorUserIds, StringComparer.Ordinal) { primary };
            bool isAdministrator = administrators.Contains(userId);
            if (isAdministrator && (WorldGenerationBaseline.ValidationFailure(configuration) is null))
            {
                var candidate = CopyWithTileGeometry(MergeSubmittedWorldGenerationBaselineLocked(
                    EnsureSubmittedLanguageFeatureNameCatalog(configuration), userId, activeWorldExtensions), tileGeometry: null);
                string? reference = PersistLocked(candidate, null, primary, administrators, pendingLayerNotifications, writeSubstrate: true);
                administratorUserId = primary;
                administratorUserIds = administrators;
                worldConfiguration = candidate;
                worldSubstratePayload = null;
                worldSubstrateKey = reference;
            }
            return new WorldSessionState(IsAdministratorLocked(userId), HasReadyWorldLocked(),
                RequiresInitialWorldConfiguration: false, administratorUserId, HasReadyWorldLocked() ? worldConfiguration : null);
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
            if (worldConfiguration is null || WorldGenerationBaseline.ValidationFailure(worldConfiguration) is not null)
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
            var candidate = CopyWithFeatureNameCatalogs(
                worldConfiguration,
                worldConfiguration.FeatureNameCatalogs
                    .Concat(new[] { catalog })
                    .OrderBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            SaveLocked(candidate, writeSubstrate: false);
            worldConfiguration = candidate;
            return new WorldFeatureNameCatalogSubmitResult(true, true, "Created", worldConfiguration);
        }
    }

    public WorldSubstrateStoreResult StoreWorldSubstrate(
        string userId,
        string colonyId,
        string worldConfigurationId,
        byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldConfigurationId);
        ArgumentNullException.ThrowIfNull(payload);

        lock (gate)
        {
            if (worldConfiguration is null || WorldGenerationBaseline.ValidationFailure(worldConfiguration) is not null)
            {
                return new WorldSubstrateStoreResult(false, "WorldNotConfigured", 0, 0, worldConfiguration);
            }

            if (!IsAdministratorLocked(userId))
            {
                return new WorldSubstrateStoreResult(false, "AdminOnly", 0, 0, worldConfiguration);
            }

            if (!string.Equals(worldConfiguration.WorldConfigurationId, worldConfigurationId, StringComparison.Ordinal))
            {
                return new WorldSubstrateStoreResult(false, "WorldConfigurationMismatch", 0, 0, worldConfiguration);
            }

            byte[] candidatePayload = (byte[])payload.Clone();
            if (!WorldSubstratePackageCodec.TryDecode(candidatePayload, out WorldSubstratePackage? package, out _)
                || package is null)
            {
                return new WorldSubstrateStoreResult(false, "InvalidWorldSubstrate", 0, 0, worldConfiguration);
            }

            WorldTileGeometryDto? geometry = WorldTileGeometryBinaryCodec.Decode(package.TileGeometryPayload);
            if (geometry is null || geometry.Layers.Count == 0 || geometry.Layers.All(layer => layer.TileCenters.Count == 0))
            {
                return new WorldSubstrateStoreResult(false, "MissingWorldSubstrateGeometry", 0, 0, worldConfiguration);
            }

            var candidate = CopyWithTileGeometry(worldConfiguration, geometry);
            string? reference = PersistLocked(candidate, candidatePayload, administratorUserId, administratorUserIds,
                pendingLayerNotifications, writeSubstrate: true);
            worldSubstratePayload = candidatePayload;
            worldConfiguration = candidate;
            worldSubstrateKey = reference;
            return new WorldSubstrateStoreResult(
                true,
                "Registered",
                geometry.Layers.Count,
                geometry.Layers.Sum(layer => layer.TileCenters.Count),
                worldConfiguration);
        }
    }

    public bool TryGetWorldSubstrate(string worldConfigurationId, out byte[]? payload)
    {
        lock (gate)
        {
            payload = null;
            if (worldConfiguration is null
                || WorldGenerationBaseline.ValidationFailure(worldConfiguration) is not null
                || !string.Equals(worldConfiguration.WorldConfigurationId, worldConfigurationId, StringComparison.Ordinal)
                || worldSubstratePayload is null
                || !WorldSubstratePackageCodec.TryDecode(worldSubstratePayload, out _, out _))
            {
                return false;
            }

            payload = (byte[])worldSubstratePayload.Clone();
            return true;
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
                    var bySite = new Dictionary<(string UserId, string ColonyId, string SiteId), PlayerColonySiteDto>();
                    foreach (PlayerColonySiteDto site in worldConfiguration.PlayerColonySites)
                    {
                        if (site.Tile >= 0
                            && !string.IsNullOrWhiteSpace(site.UserId)
                            && !string.IsNullOrWhiteSpace(site.ColonyId)
                            && (!string.Equals(site.UserId, userId, StringComparison.Ordinal)
                                || !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)))
                        {
                            bySite[WorldColonySites.Key(site)] = site;
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
                            bySite[WorldColonySites.Key(normalized)] = normalized;
                        }
                    }

                    mergedSites = bySite.Values
                        .OrderBy(site => site.Tile)
                        .ThenBy(site => site.UserId, StringComparer.Ordinal)
                        .ThenBy(site => site.ColonyId, StringComparer.Ordinal)
                        .ToList();
                }

                WorldConfigurationExtensionService extensionsService = ResolveWorldExtensions(activeWorldExtensions);
                WorldConfigurationDto candidate = CopyWithPlayerColonySitesAndExtensions(
                    worldConfiguration,
                    mergedSites,
                    extensionsService.MergeExtensions(
                        new WorldConfigurationExtensionContext(
                            userId,
                            colonyId,
                            worldConfiguration.WorldConfigurationId),
                        worldConfiguration.Extensions,
                        extensions ?? Array.Empty<WorldConfigurationExtensionDto>()));
                // Registration changes sites/extensions, not the substrate. Publish
                // only after persistence so upload recovery still sees the old state.
                SaveLocked(candidate, writeSubstrate: false);
                worldConfiguration = candidate;
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
                    WorldConfigurationDto candidate = CopyWithPlayerColonySitesAndExtensions(
                        worldConfiguration,
                        retained,
                        extensionsService.RemoveColonyExtensions(
                            userId,
                            colonyId,
                            worldConfiguration.Extensions));
                    // Site removal does not change the substrate. Persist the candidate
                    // before publishing it, so a failed deletion can be retried in-process.
                    SaveLocked(candidate, writeSubstrate: false);
                    worldConfiguration = candidate;
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
        => ConfirmWorldTileFloatLayerCore(layerId, values, activeWorldExtensions, null);

    internal IReadOnlyList<WorldTileFloatLayerIncrease> ConfirmWorldTileFloatLayerWithNotifications(
        string layerId, IReadOnlyList<WorldTileFloatLayerValue> values, WorldConfigurationExtensionService activeWorldExtensions,
        Func<IReadOnlyList<WorldTileFloatLayerIncrease>, WorldLayerNotificationWork> prepareNotifications)
        => ConfirmWorldTileFloatLayerCore(layerId, values, activeWorldExtensions, prepareNotifications);

    private IReadOnlyList<WorldTileFloatLayerIncrease> ConfirmWorldTileFloatLayerCore(
        string layerId, IReadOnlyList<WorldTileFloatLayerValue> values, WorldConfigurationExtensionService? activeWorldExtensions,
        Func<IReadOnlyList<WorldTileFloatLayerIncrease>, WorldLayerNotificationWork>? prepareNotifications)
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
                WorldConfigurationDto candidate = CopyWithTileFloatLayer(
                    worldConfiguration,
                    layerId,
                    current
                        .Where(pair => pair.Value > 0f)
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new WorldTileFloatLayerValue(pair.Key, pair.Value))
                        .ToList(),
                    extensionsService);
                var pending = pendingLayerNotifications;
                if (prepareNotifications is not null)
                {
                    WorldLayerNotificationWork work = prepareNotifications(increases.ToArray());
                    work.Validate();
                    if (work.WorldConfigurationId != worldConfiguration.WorldConfigurationId || work.LayerId != layerId
                        || !work.Increases.SequenceEqual(increases) || pending.Any(item => item.Id == work.Id))
                        throw new InvalidOperationException("World-layer work does not match its projection.");
                    pending = pending.Append(work).ToArray();
                }
                // The configuration and its fixed notifications share one durable write.
                SaveLocked(candidate, writeSubstrate: false, pending);
                worldConfiguration = candidate;
                pendingLayerNotifications = pending;
            }

            return increases;
        }
    }

    private static IReadOnlyList<PlayerColonySiteDto> MergePlayerColonySites(
        IReadOnlyList<PlayerColonySiteDto> current,
        IReadOnlyList<PlayerColonySiteDto>? incoming)
    {
        var bySite = new Dictionary<(string UserId, string ColonyId, string SiteId), PlayerColonySiteDto>();
        foreach (PlayerColonySiteDto site in current)
        {
            if (site.Tile >= 0
                && !string.IsNullOrWhiteSpace(site.UserId)
                && !string.IsNullOrWhiteSpace(site.ColonyId))
            {
                bySite[WorldColonySites.Key(site)] = site;
            }
        }

        foreach (PlayerColonySiteDto site in incoming ?? Array.Empty<PlayerColonySiteDto>())
        {
            if (site.Tile >= 0
                && !string.IsNullOrWhiteSpace(site.UserId)
                && !string.IsNullOrWhiteSpace(site.ColonyId))
            {
                bySite[WorldColonySites.Key(site)] = site;
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
            if (!WorldFeatureGeometry.From(expected).Matches(WorldFeatureGeometry.From(actual)))
            {
                failureReason = "FeatureFingerprintMismatch";
                return false;
            }
        }

        failureReason = null;
        return true;
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
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            SaveLocked();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        try
        {
            IReadOnlyDictionary<string, string> rows = structuredPersistence.ReadAll();
            if (rows.Count == 0)
            {
                return;
            }

            pendingLayerNotifications = rows.TryGetValue("pending-layer-notifications", out string? pendingJson)
                ? ReadPendingLayerNotifications(pendingJson) : [];

            worldSubstrateKey = WorldSubstratePersistence.ReadKey(rows.GetValueOrDefault("state"));
            WorldConfigurationStateRecord? state = TryDeserializeRow<WorldConfigurationStateRecord>(rows, "state");
            administratorUserId = string.IsNullOrWhiteSpace(state?.AdministratorUserId)
                ? null
                : state!.AdministratorUserId;
            administratorUserIds = state?.AdministratorUserIds is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : state.AdministratorUserIds
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .ToHashSet(StringComparer.Ordinal);
            EnsurePrimaryAdministratorLocked();

            WorldConfigurationCoreRecord? core = TryDeserializeRow<WorldConfigurationCoreRecord>(rows, "core");
            if (core is null)
            {
                if (!string.IsNullOrEmpty(worldSubstrateKey)) throw new InvalidOperationException("World substrate reference has no configuration.");
                worldConfiguration = null;
                return;
            }

            IReadOnlyList<WorldFeatureNameCatalogDto> catalogs = rows
                .Where(pair => pair.Key.StartsWith("feature-name-catalog:", StringComparison.Ordinal))
                .Select(pair => TryDeserialize<WorldFeatureNameCatalogDto>(pair.Value))
                .Where(catalog => catalog is not null)
                .Cast<WorldFeatureNameCatalogDto>()
                .OrderBy(catalog => catalog.Language, StringComparer.OrdinalIgnoreCase)
                .ToList();

            worldSubstratePayload = WorldSubstratePersistence.Read(binaryPersistence, worldSubstrateKey);
            WorldSubstratePackageCodec.TryDecode(worldSubstratePayload ?? Array.Empty<byte>(), out WorldSubstratePackage? substrate, out _);
            worldConfiguration = core.ToDto(
                TryDeserializeRow<WorldConfigurationListRecord<WorldFeatureDto>>(rows, "features")?.Values,
                TryDeserializeRow<WorldConfigurationListRecord<WorldFactionDto>>(rows, "factions")?.Values,
                TryDeserializeRow<WorldConfigurationListRecord<WorldRoadDto>>(rows, "roads")?.Values,
                TryDeserializeRow<WorldConfigurationListRecord<WorldObjectBaselineDto>>(rows, "world-objects")?.Values,
                TryDeserializeRow<WorldConfigurationListRecord<PlayerColonySiteDto>>(rows, "player-colony-sites")?.Values,
                substrate is null ? null : WorldTileGeometryBinaryCodec.Decode(substrate.TileGeometryPayload),
                TryDeserializeRow<WorldConfigurationListRecord<WorldConfigurationExtensionDto>>(rows, "extensions")?.Values,
                catalogs);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new InvalidOperationException("Cannot load persisted world configuration.", ex); }
    }

    private bool LoadLegacyReadOnly()
    {
        if (legacyPersistence is null)
        {
            return false;
        }

        try
        {
            string? json = legacyPersistence.Read();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            worldSubstrateKey = WorldSubstratePersistence.ReadKey(json);
            using (JsonDocument document = JsonDocument.Parse(json))
                pendingLayerNotifications = document.RootElement.TryGetProperty("PendingLayerNotifications", out var pendingJson)
                    ? ReadPendingLayerNotifications(pendingJson.GetRawText()) : [];

            WorldConfigurationPersistence? persisted =
                JsonSerializer.Deserialize<WorldConfigurationPersistence>(json, JsonOptions);
            if (persisted?.WorldConfiguration is null && !string.IsNullOrEmpty(worldSubstrateKey))
                throw new InvalidOperationException("World substrate reference has no configuration.");
            administratorUserId = string.IsNullOrWhiteSpace(persisted?.AdministratorUserId)
                ? null
                : persisted!.AdministratorUserId;
            administratorUserIds = persisted?.AdministratorUserIds is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : persisted.AdministratorUserIds
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .ToHashSet(StringComparer.Ordinal);
            EnsurePrimaryAdministratorLocked();
            worldSubstratePayload = WorldSubstratePersistence.Read(binaryPersistence, worldSubstrateKey);
            worldConfiguration = RestorePersistedWorldConfiguration(persisted);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new InvalidOperationException("Cannot load persisted world configuration.", ex); }
    }

    private void SaveLocked()
        => SaveLocked(worldConfiguration, writeSubstrate: true);

    private void SaveLocked(WorldConfigurationDto? configuration, bool writeSubstrate,
        IReadOnlyList<WorldLayerNotificationWork>? pending = null)
    {
        worldSubstrateKey = PersistLocked(configuration, worldSubstratePayload, administratorUserId, administratorUserIds,
            pending ?? pendingLayerNotifications, writeSubstrate);
    }

    private void PersistAdministratorsLocked(string? primary, HashSet<string> administrators)
    {
        PersistLocked(worldConfiguration, worldSubstratePayload, primary, administrators, pendingLayerNotifications, writeSubstrate: false);
        administratorUserId = primary;
        administratorUserIds = administrators;
    }

    private string? PersistLocked(WorldConfigurationDto? configuration, byte[]? substrate,
        string? primary, IReadOnlyCollection<string> administrators, IReadOnlyList<WorldLayerNotificationWork> pending, bool writeSubstrate)
    {
        bool persistent = structuredPersistence is not null || legacyPersistence is not null;
        if (persistent && writeSubstrate && substrate is not null && binaryPersistence is null)
            throw new InvalidOperationException("Persistent world configuration requires substrate storage.");
        // A reset publishes an explicit empty reference, never deletes the previous blob.
        string? reference = writeSubstrate ? WorldSubstratePersistence.Store(persistent ? binaryPersistence : null, substrate) : worldSubstrateKey;
        if (structuredPersistence is not null)
            structuredPersistence.ReplaceAll(BuildStructuredRows(configuration, pending, primary, administrators, reference));
        else if (legacyPersistence is not null)
            legacyPersistence.Write(JsonSerializer.Serialize(BuildPersistenceSnapshot(primary, administrators, configuration)
                with { PendingLayerNotifications = pending, WorldSubstrateKey = reference }, JsonOptions));
        return reference;
    }

    private static IReadOnlyDictionary<string, string> BuildStructuredRows(WorldConfigurationDto? configuration,
        IReadOnlyList<WorldLayerNotificationWork> pending, string? primary, IReadOnlyCollection<string> administrators, string? reference)
    {
        Dictionary<string, string> rows = new(StringComparer.Ordinal)
        {
            ["pending-layer-notifications"] = JsonSerializer.Serialize(pending, JsonOptions),
            ["state"] = JsonSerializer.Serialize(
                new WorldConfigurationStateRecord(primary, administrators.ToList(), reference),
                JsonOptions)
        };

        if (configuration is null)
        {
            return rows;
        }

        rows["core"] = JsonSerializer.Serialize(WorldConfigurationCoreRecord.FromDto(configuration), JsonOptions);
        rows["features"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<WorldFeatureDto>(configuration.Features), JsonOptions);
        rows["factions"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<WorldFactionDto>(configuration.Factions), JsonOptions);
        rows["roads"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<WorldRoadDto>(configuration.Roads), JsonOptions);
        rows["world-objects"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<WorldObjectBaselineDto>(configuration.WorldObjects), JsonOptions);
        rows["player-colony-sites"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<PlayerColonySiteDto>(configuration.PlayerColonySites), JsonOptions);
        rows["extensions"] = JsonSerializer.Serialize(new WorldConfigurationListRecord<WorldConfigurationExtensionDto>(configuration.Extensions), JsonOptions);
        foreach (WorldFeatureNameCatalogDto catalog in configuration.FeatureNameCatalogs)
        {
            if (string.IsNullOrWhiteSpace(catalog.Language))
            {
                continue;
            }

            rows["feature-name-catalog:" + catalog.Language] = JsonSerializer.Serialize(catalog, JsonOptions);
        }

        return rows;
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

    private bool HasReadyWorldLocked()
    {
        return worldConfiguration is not null
            && (WorldGenerationBaseline.ValidationFailure(worldConfiguration) is null)
            && worldSubstratePayload is not null
            && WorldSubstratePackageCodec.TryDecode(worldSubstratePayload, out _, out _);
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

        WorldSubstratePackageCodec.TryDecode(worldSubstratePayload ?? Array.Empty<byte>(), out WorldSubstratePackage? substrate, out _);
        WorldTileGeometryDto? restoredGeometry = substrate is null
            ? null
            : WorldTileGeometryBinaryCodec.Decode(substrate.TileGeometryPayload);
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

    private static T? TryDeserializeRow<T>(IReadOnlyDictionary<string, string> rows, string key)
    {
        return rows.TryGetValue(key, out string? json)
            ? TryDeserialize<T>(json)
            : default;
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed record WorldConfigurationStateRecord(
        string? AdministratorUserId,
        IReadOnlyList<string>? AdministratorUserIds,
        string? WorldSubstrateKey = null);

    private sealed record WorldConfigurationListRecord<T>(IReadOnlyList<T>? Values);

    private sealed record WorldConfigurationCoreRecord(
        string WorldConfigurationId,
        string ConfiguredByUserId,
        string ConfiguredByColonyId,
        DateTimeOffset ConfiguredAtUtc,
        string? SeedString,
        string? PlanetCoverage,
        string? OverallRainfall,
        string? OverallTemperature,
        string? OverallPopulation,
        string? LandmarkDensity,
        string? TileCount,
        IReadOnlyList<string>? FactionDefNames,
        string? StorytellerDefName,
        string? DifficultyDefName,
        string? DifficultyValuesXml,
        string? GameLanguage)
    {
        public static WorldConfigurationCoreRecord FromDto(WorldConfigurationDto configuration)
        {
            return new WorldConfigurationCoreRecord(
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
                configuration.StorytellerDefName,
                configuration.DifficultyDefName,
                configuration.DifficultyValuesXml,
                configuration.GameLanguage);
        }

        public WorldConfigurationDto ToDto(
            IReadOnlyList<WorldFeatureDto>? features,
            IReadOnlyList<WorldFactionDto>? factions,
            IReadOnlyList<WorldRoadDto>? roads,
            IReadOnlyList<WorldObjectBaselineDto>? worldObjects,
            IReadOnlyList<PlayerColonySiteDto>? playerColonySites,
            WorldTileGeometryDto? tileGeometry,
            IReadOnlyList<WorldConfigurationExtensionDto>? extensions,
            IReadOnlyList<WorldFeatureNameCatalogDto>? featureNameCatalogs)
        {
            return new WorldConfigurationDto(
                WorldConfigurationId,
                ConfiguredByUserId,
                ConfiguredByColonyId,
                ConfiguredAtUtc,
                SeedString,
                PlanetCoverage,
                OverallRainfall,
                OverallTemperature,
                OverallPopulation,
                LandmarkDensity,
                TileCount,
                FactionDefNames,
                features,
                factions,
                roads,
                worldObjects,
                playerColonySites,
                StorytellerDefName,
                DifficultyDefName,
                tileGeometry,
                DifficultyValuesXml,
                extensions,
                GameLanguage,
                featureNameCatalogs);
        }
    }

    private static IReadOnlyList<WorldLayerNotificationWork> ReadPendingLayerNotifications(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<WorldLayerNotificationWork[]>(json, JsonOptions)
                ?? throw new InvalidOperationException("Missing pending world-layer notification list.");
            foreach (var work in result)
            {
                if (work is null) throw new InvalidOperationException("Null pending world-layer notification work.");
                work.Validate();
            }
            if (result.Select(work => work.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
                throw new InvalidOperationException("Duplicate pending world-layer notification identity.");
            return result;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Unreadable pending world-layer notifications.", ex); }
    }

    private sealed record WorldConfigurationPersistence(
        string? AdministratorUserId,
        IReadOnlyList<string>? AdministratorUserIds,
        WorldConfigurationDto? WorldConfiguration,
        IReadOnlyList<WorldLayerNotificationWork>? PendingLayerNotifications = null,
        string? WorldSubstrateKey = null);
}

public sealed record WorldSessionState(
    bool IsAdministrator,
    bool WorldConfigured,
    bool RequiresInitialWorldConfiguration,
    string? AdministratorUserId,
    WorldConfigurationDto? WorldConfiguration);

public sealed record RemovePlayerColonySitesResult(int RemovedCount, WorldSessionState Session);

public sealed record WorldSubstrateStoreResult(
    bool Accepted,
    string Message,
    int LayerCount,
    int TileCenterCount,
    WorldConfigurationDto? WorldConfiguration);

public sealed record WorldFeatureNameCatalogSubmitResult(
    bool Accepted,
    bool Created,
    string Message,
    WorldConfigurationDto? WorldConfiguration);
