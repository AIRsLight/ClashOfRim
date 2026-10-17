using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult GetAdminBaselineRequirements(GetAdminBaselineRequirementsRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolResponse failure) => Results.Ok(new GetAdminBaselineRequirementsResponse(
            failure, false, state.AdminBaseline.Current is not null, null));
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AdminBaseline.MissingIdentity")));
        using (var recovery = EnterRecoveredSnapshotMutation(state,
            () => AuthorizeAdministratorRequest(state, request.UserId, request.ColonyId, request.AuthToken)))
        {
            if (recovery.Rejection is { } failure) return Reject(failure);
            WorldSessionState session = state.WorldConfiguration.GetSessionState(request.UserId);
            IReadOnlySet<string> packageIds = BuildServerPackageIdSet(state.CompatibilityBaseline.Current);
            var context = new AdminBaselineRequirementContext(packageIds);
            List<AdminBaselineExtensionRequirementDto> requirements = state.Plugins.AdminBaselineRequirementProviders
                .SelectMany(provider => SafeAdminBaselineRequirements(provider, context))
                .Where(requirement => !string.IsNullOrWhiteSpace(requirement.ProviderId)
                    && !string.IsNullOrWhiteSpace(requirement.Kind))
                .GroupBy(requirement => requirement.ProviderId + "\u001f" + requirement.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(requirement => requirement.ProviderId, StringComparer.Ordinal)
                .ThenBy(requirement => requirement.Kind, StringComparer.Ordinal)
                .Select(requirement => new AdminBaselineExtensionRequirementDto(
                    requirement.ProviderId,
                    requirement.Kind,
                    requirement.RequiredPackageId,
                    requirement.DisplayName))
                .ToList();

            return Results.Ok(new GetAdminBaselineRequirementsResponse(
                ProtocolResponse.Ok(T("AdminBaseline.RequirementsPrepared")),
                isAdministrator: true,
                baselineConfigured: state.AdminBaseline.Current is not null,
                session.AdministratorUserId,
                requirements));
        }
    }

    private static IEnumerable<AdminBaselineExtensionRequirement> SafeAdminBaselineRequirements(
        IAdminBaselineRequirementProvider provider,
        AdminBaselineRequirementContext context)
    {
        try
        {
            return provider.GetRequirements(context) ?? Array.Empty<AdminBaselineExtensionRequirement>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] AdminBaselineRequirementProviderCallbackException: "
                + provider.GetType().FullName
                + " "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return Array.Empty<AdminBaselineExtensionRequirement>();
        }
    }

    private static IReadOnlySet<string> BuildServerPackageIdSet(CompatibilityManifest? manifest)
    {
        return new HashSet<string>(
            (manifest?.Mods ?? Array.Empty<ModManifestEntry>())
            .Select(mod => mod.PackageId)
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IResult SubmitAdminBaseline(SubmitAdminBaselineRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolResponse failure) => Results.Ok(new SubmitAdminBaselineResponse(
            failure, false, state.AdminBaseline.Current is not null, null, 0, 0, 0, 0, 0, 0, 0));
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AdminBaseline.MissingIdentity")));
        ProtocolResponse? Authorize() => AuthorizeAdministratorRequest(state, request.UserId, request.ColonyId, request.AuthToken);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);
        // Normalize potentially large price/definition collections without owning the mutation gate.
        AdminBaselineSnapshot normalized = AdminBaselineSnapshot.FromRequest(request);
        using (var recovery = EnterRecoveredSnapshotMutation(state, Authorize))
        {
            if (recovery.Rejection is { } failure) return Reject(failure);
            WorldSessionState session = state.WorldConfiguration.GetSessionState(request.UserId);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var invalidations = state.TradeLifecycle.PrepareTradeOrderBaselineInvalidations(normalized, nowUtc);
            AdminBaselineSnapshot baseline = state.AdminBaseline.SubmitPrepared(normalized, invalidations);
            state.TradeLifecycle.RecoverAdminBaselineTradeInvalidations();
            int cancelledTradeOrderCount = invalidations.Count;
            if (!string.IsNullOrWhiteSpace(request.ColonyId))
            {
                RecordPlayerSeen(state, request.UserId, request.ColonyId!, currentSnapshotId: null, nowUtc);
            }

            return Results.Ok(new SubmitAdminBaselineResponse(
                ProtocolResponse.Ok(cancelledTradeOrderCount > 0
                    ? T("AdminBaseline.RegisteredWithCancelledTrades", ("COUNT", cancelledTradeOrderCount.ToString(CultureInfo.InvariantCulture)))
                    : T("AdminBaseline.Registered")),
                isAdministrator: true,
                baselineConfigured: true,
                session.AdministratorUserId,
                baseline.StandardMarketValuePerThing.Count,
                baseline.TrapAutoApprovedCount,
                baseline.TrapCandidateCount,
                baseline.TrapApprovedCount,
                baseline.PackableBuildingCount,
                baseline.BuildingCount,
                baseline.BaselineExtensionCount));
        }
    }

    private static string BuildWorldSessionMessage(WorldSessionState session)
    {
        if (session.WorldConfigured)
        {
            return T("WorldSession.ExistingConfiguration");
        }

        return session.IsAdministrator
            ? T("WorldSession.FirstAdminSetup")
            : T("WorldSession.WaitingForAdmin");
    }

    private static WorldConfigurationDto NormalizeWorldConfiguration(
        WorldConfigurationDto configuration,
        string userId,
        string colonyId,
        WorldConfigurationExtensionService worldExtensions)
    {
        string worldConfigurationId = string.IsNullOrWhiteSpace(configuration.WorldConfigurationId)
            ? $"world:{Guid.NewGuid():N}"
            : configuration.WorldConfigurationId;
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            worldExtensions.NormalizeSubmittedExtensions(
                new WorldConfigurationExtensionContext(userId, colonyId, worldConfigurationId),
                configuration.Extensions);
        return new WorldConfigurationDto(
            worldConfigurationId,
            userId,
            colonyId,
            configuration.ConfiguredAtUtc == default ? DateTimeOffset.UtcNow : configuration.ConfiguredAtUtc,
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
            extensions,
            configuration.GameLanguage,
            configuration.FeatureNameCatalogs);
    }

    private static bool HasUsableWorldGenerationBaseline(WorldConfigurationDto configuration, out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(configuration.SeedString))
        {
            failureReason = T("WorldConfiguration.MissingSeed");
            return false;
        }

        if (!float.TryParse(
                configuration.PlanetCoverage,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float coverage)
            || coverage <= 0f)
        {
            failureReason = T("WorldConfiguration.MissingPlanetCoverage");
            return false;
        }

        if (configuration.FactionDefNames.Count == 0)
        {
            failureReason = T("WorldConfiguration.MissingFactions");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static WorldConfigurationDto? BuildWorldConfigurationForDelivery(
        WorldConfigurationDto? configuration,
        ClashOfRimNetworkState state,
        bool includeGenerationBaseline = true,
        bool includePlayerColonySites = true,
        bool includeWorldExtensions = true)
    {
        if (configuration is null)
        {
            return null;
        }

        IReadOnlyList<PlayerColonySiteDto> occupiedSites = includePlayerColonySites
            ? BuildCurrentPlayerColonySites(configuration)
            : Array.Empty<PlayerColonySiteDto>();
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            ActiveWorldConfigurationExtensions(state).BuildDeliveryExtensions(
                includeGenerationBaseline ? configuration.Extensions : Array.Empty<WorldConfigurationExtensionDto>(),
                includeWorldExtensions);
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            includeGenerationBaseline ? configuration.SeedString : null,
            includeGenerationBaseline ? configuration.PlanetCoverage : null,
            includeGenerationBaseline ? configuration.OverallRainfall : null,
            includeGenerationBaseline ? configuration.OverallTemperature : null,
            includeGenerationBaseline ? configuration.OverallPopulation : null,
            includeGenerationBaseline ? configuration.LandmarkDensity : null,
            includeGenerationBaseline ? configuration.TileCount : null,
            includeGenerationBaseline ? configuration.FactionDefNames : Array.Empty<string>(),
            includeGenerationBaseline ? configuration.Features : Array.Empty<WorldFeatureDto>(),
            includeGenerationBaseline ? configuration.Factions : Array.Empty<WorldFactionDto>(),
            includeGenerationBaseline ? configuration.Roads : Array.Empty<WorldRoadDto>(),
            includeGenerationBaseline ? configuration.WorldObjects : Array.Empty<WorldObjectBaselineDto>(),
            occupiedSites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            tileGeometry: null,
            difficultyValuesXml: configuration.DifficultyValuesXml,
            extensions: extensions,
            gameLanguage: configuration.GameLanguage,
            featureNameCatalogs: includeGenerationBaseline ? configuration.FeatureNameCatalogs : Array.Empty<WorldFeatureNameCatalogDto>());
    }

    private static IReadOnlyList<PlayerColonySiteDto> BuildCurrentPlayerColonySites(
        WorldConfigurationDto configuration)
    {
        var bySite = new Dictionary<string, PlayerColonySiteDto>(StringComparer.Ordinal);
        foreach (PlayerColonySiteDto site in configuration.PlayerColonySites)
        {
            if (site.Tile >= 0 && !string.IsNullOrWhiteSpace(site.UserId) && !string.IsNullOrWhiteSpace(site.ColonyId))
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

    private static IReadOnlyList<PlayerColonySiteDto> NormalizePlayerColonySites(
        string userId,
        string colonyId,
        IEnumerable<PlayerColonySiteDto> sites)
    {
        var bySite = new Dictionary<string, PlayerColonySiteDto>(StringComparer.Ordinal);
        foreach (PlayerColonySiteDto site in sites)
        {
            if (site.Tile < 0)
            {
                continue;
            }

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

        return bySite.Values
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.MapUniqueId, StringComparer.Ordinal)
            .ToList();
    }

    private static PlayerColonySiteDto? FindSameTilePlayerColonyConflict(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IEnumerable<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        HashSet<(int Tile, int LayerId)> requestedTiles = requestedSites
            .Select(site => (site.Tile, Math.Max(0, site.TileLayerId)))
            .ToHashSet();
        return existingSites.FirstOrDefault(site =>
            requestedTiles.Contains((site.Tile, Math.Max(0, site.TileLayerId)))
            && (!string.Equals(site.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)));
    }

    private static PlayerColonySiteDto? FindNearbyPlayerColonyConflict(
        ClashOfRimNetworkState state,
        IEnumerable<PlayerColonySiteDto> existingSites,
        IEnumerable<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        WorldTileGeometryDistanceSource? geometry = BuildWorldTileGeometryDistanceSource(state.WorldConfiguration.Current);
        if (geometry is null)
        {
            return null;
        }

        foreach (PlayerColonySiteDto requested in requestedSites.Where(site => site.Tile >= 0))
        {
            foreach (PlayerColonySiteDto existing in existingSites.Where(site => site.Tile >= 0))
            {
                if (string.Equals(existing.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(existing.ColonyId, colonyId, StringComparison.Ordinal))
                {
                    continue;
                }

                int? distance = state.WorldTileDistanceCalculator.TryCalculateDistance(
                    geometry,
                    new WorldTileRef(requested.Tile, Math.Max(0, requested.TileLayerId)),
                    new WorldTileRef(existing.Tile, Math.Max(0, existing.TileLayerId)),
                    crossLayerOverheadDistanceTiles: 0);
                if (distance is 1)
                {
                    return existing;
                }
            }
        }

        return null;
    }

    private static string? FindExistingUserColonyConflict(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IReadOnlyList<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        PlayerColonySiteDto? requested = requestedSites.FirstOrDefault();
        if (requested is null)
        {
            return null;
        }

        List<PlayerColonySiteDto> userSites = existingSites
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal) && site.Tile >= 0)
            .GroupBy(ColonySiteKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (userSites.Count == 0)
        {
            return null;
        }

        PlayerColonySiteDto? otherColony = userSites.FirstOrDefault(site =>
            !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal));
        if (otherColony is not null)
        {
            return T("WorldColonySites.PlayerAlreadyHasNamedColony", ("USER", userId), ("COLONY", otherColony.ColonyId));
        }

        if (userSites.All(site => string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)))
        {
            return null;
        }

        return T("WorldColonySites.PlayerAlreadyHasColony", ("USER", userId));
    }

    private static PlayerColonySiteDto? FindExistingSameColonySiteOnDifferentTile(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IReadOnlyList<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        PlayerColonySiteDto? requested = requestedSites.FirstOrDefault();
        if (requested is null || requested.Tile < 0)
        {
            return null;
        }

        return existingSites
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)
                && site.Tile >= 0)
            .GroupBy(ColonySiteKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .FirstOrDefault(site => site.Tile != requested.Tile
                || Math.Max(0, site.TileLayerId) != Math.Max(0, requested.TileLayerId));
    }

    private static string ColonySiteKey(PlayerColonySiteDto site)
    {
        string stableSiteId = !string.IsNullOrWhiteSpace(site.WorldObjectId)
            ? site.WorldObjectId!
            : !string.IsNullOrWhiteSpace(site.MapUniqueId)
                ? site.MapUniqueId!
                : "tile:" + site.Tile.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + Math.Max(0, site.TileLayerId).ToString(CultureInfo.InvariantCulture);
        return $"{site.UserId}\u001f{site.ColonyId}\u001f{stableSiteId}";
    }

    private static IReadOnlyList<CompatibilityIssue> BuildWorldLanguageIssues(
        WorldConfigurationDto? configuration,
        string? clientLanguage)
    {
        var issues = new List<CompatibilityIssue>();
        CompatibilityManifestComparer.CompareGameLanguage(
            issues,
            configuration?.GameLanguage,
            clientLanguage);
        return issues;
    }

    private static IResult ChangeOfflinePassword(ChangeOfflinePasswordRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!state.AuthTokens.IsValid(request.AuthToken, request.UserId, request.ColonyId, nowUtc))
        {
            return Results.Ok(new ChangeOfflinePasswordResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("OfflineAuth.PasswordChangeUnauthorized"))));
        }

        bool changed = state.OfflineAccounts.ChangePassword(
            request.UserId,
            request.CurrentPassword,
            request.NewPassword,
            nowUtc,
            out string failure);
        return Results.Ok(new ChangeOfflinePasswordResponse(changed
            ? ProtocolResponse.Ok(T("OfflineAuth.PasswordChanged"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, CredentialAuthentication.LocalizeOfflineFailure(failure))));
    }

    private static IResult OverrideCompatibilityBaseline(
        OverrideCompatibilityBaselineRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AuthenticationValidationResult auth = ValidateAuthentication(state, request.UserId, request.SteamAuthTicket, request.Password, nowUtc);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                state.CompatibilityBaseline.Current is null ? null : SerializeCompatibilityManifest(state.CompatibilityBaseline.Current, state),
                updatedAtUtc: null));
        }

        string authenticatedUserId = auth.AuthenticatedUserId!.Trim();
        if (!state.WorldConfiguration.IsAdministrator(authenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Compatibility.OverrideAdminOnly")),
                state.CompatibilityBaseline.Current is null ? null : SerializeCompatibilityManifest(state.CompatibilityBaseline.Current, state),
                updatedAtUtc: null));
        }

        if (!TryReadCompatibilityManifest(
                request.CompatibilityManifestJson,
                out CompatibilityManifest? manifest,
                out string failure)
            || manifest is null)
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, failure),
                state.CompatibilityBaseline.Current is null ? null : SerializeCompatibilityManifest(state.CompatibilityBaseline.Current, state),
                updatedAtUtc: null));
        }

        CompatibilityBaselineUpdateResult update = state.CompatibilityBaseline.ReplaceBaseline(
            manifest,
            authenticatedUserId,
            nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "兼容清单基线已覆盖：user={UserId} manifest={ManifestId} mods={ModCount} dlcs={DlcCount}",
            authenticatedUserId,
            manifest.ManifestId,
            manifest.Mods.Count,
            manifest.DlcIds.Count);
        return Results.Ok(new OverrideCompatibilityBaselineResponse(
            ProtocolResponse.Ok(T("Compatibility.OverrideSuccess")),
            update.Baseline is null ? null : SerializeCompatibilityManifest(update.Baseline, state),
            update.UpdatedAtUtc));
    }

    private static bool TryReadCompatibilityManifest(
        string? manifestJson,
        out CompatibilityManifest? manifest,
        out string failure)
    {
        manifest = null;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return true;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<CompatibilityManifest>(manifestJson, CompatibilityJsonOptions);
            if (manifest is null)
            {
                failure = T("Compatibility.EmptyManifest");
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            failure = T("Compatibility.InvalidManifestJson", ("MESSAGE", ex.Message));
            return false;
        }
    }

    private static bool TryReadCompatibilityManifestSummary(
        string? summaryJson,
        out CompatibilityManifestSummary? summary,
        out string failure)
    {
        summary = null;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            failure = T("Compatibility.ManifestMissing");
            return false;
        }

        try
        {
            summary = JsonSerializer.Deserialize<CompatibilityManifestSummary>(summaryJson, CompatibilityJsonOptions);
            if (summary is null)
            {
                failure = T("Compatibility.EmptyManifest");
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            failure = T("Compatibility.InvalidManifestJson", ("MESSAGE", ex.Message));
            return false;
        }
    }

    private static CompatibilitySummaryHandshake CompareCompatibilitySummary(
        CompatibilityManifest server,
        CompatibilityManifestSummary client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        CompareSummaryScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareSummaryScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareSummaryScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSummarySequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
        CompareSummaryDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        var serverById = server.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        var clientById = client.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        var requested = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModManifestEntry serverMod in server.Mods.OrderBy(mod => mod.LoadOrder))
        {
            string key = NormalizeCompatibilityId(serverMod.PackageId);
            if (!clientById.TryGetValue(key, out ModManifestSummaryEntry? clientMod))
            {
                if (serverMod.Role is ModCompatibilityRole.Optional or ModCompatibilityRole.OptionalPureTranslation)
                {
                    issues.Add(new CompatibilityIssue(
                        CompatibilityIssueSeverity.Info,
                        CompatibilityIssueCode.MissingMod,
                        $"Optional mod not installed: {serverMod.PackageId}",
                        serverMod.PackageId));
                    continue;
                }

                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.MissingMod,
                    $"Missing required mod: {serverMod.PackageId}",
                    serverMod.PackageId));
                continue;
            }

            if (!string.Equals(ComputeServerModHash(serverMod), clientMod.Hash, StringComparison.OrdinalIgnoreCase))
            {
                requested.Add(serverMod.PackageId);
            }
        }

        foreach (ModManifestSummaryEntry clientMod in client.Mods)
        {
            if (!serverById.ContainsKey(NormalizeCompatibilityId(clientMod.PackageId)))
            {
                if (options.AllowExtraPureTranslationMods && clientMod.Role == ModCompatibilityRole.OptionalPureTranslation)
                {
                    issues.Add(new CompatibilityIssue(
                        CompatibilityIssueSeverity.Info,
                        CompatibilityIssueCode.AllowedPureTranslationMod,
                        $"Allowed extra pure translation mod: {clientMod.PackageId}",
                        clientMod.PackageId));
                    continue;
                }

                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.UnexpectedMod,
                    $"Unexpected unapproved mod: {clientMod.PackageId}",
                    clientMod.PackageId));
            }
        }

        string[] serverOrder = server.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeCompatibilityId(mod.PackageId))
            .ToArray();
        string[] clientOrder = client.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeCompatibilityId(mod.PackageId))
            .ToArray();
        if (!serverOrder.SequenceEqual(clientOrder))
        {
            issues.Add(new CompatibilityIssue(
                CompatibilityIssueSeverity.Error,
                CompatibilityIssueCode.ModOrderMismatch,
                "Mod load order mismatch, or an unapproved non-translation mod is present",
                "mods"));
        }

        bool hasError = issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error);
        if (hasError)
        {
            return new CompatibilitySummaryHandshake(false, false, Array.Empty<string>(), issues);
        }

        if (requested.Count > 0)
        {
            return new CompatibilitySummaryHandshake(false, true, requested.ToArray(), issues);
        }

        return new CompatibilitySummaryHandshake(true, false, Array.Empty<string>(), issues);
    }

    private static bool IsPartialCompatibilityManifest(CompatibilityManifest server, CompatibilityManifest client)
    {
        return client.Mods.Count > 0 && client.Mods.Count < server.Mods.Count;
    }

    private static CompatibilityComparisonResult ComparePartialCompatibilityManifest(
        CompatibilityManifest server,
        CompatibilityManifest client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        CompareSummaryScalar(issues, server.SchemaVersion, client.SchemaVersion, CompatibilityIssueCode.SchemaVersionMismatch, "Manifest schema version mismatch", "schemaVersion");
        CompareSummaryScalar(issues, server.ProtocolVersion, client.ProtocolVersion, CompatibilityIssueCode.ProtocolVersionMismatch, "Protocol version mismatch", "protocolVersion");
        CompareSummaryScalar(issues, server.RimWorldVersion, client.RimWorldVersion, CompatibilityIssueCode.RimWorldVersionMismatch, "RimWorld version mismatch", "rimWorldVersion");
        CompareSummarySequence(issues, server.DlcIds, client.DlcIds, CompatibilityIssueCode.DlcListMismatch, "Enabled DLC list mismatch", "dlcIds");
        CompareSummaryDefSummaries(issues, server.DefSummaries, client.DefSummaries);

        var serverById = server.Mods.ToDictionary(mod => NormalizeCompatibilityId(mod.PackageId), mod => mod);
        foreach (ModManifestEntry clientMod in client.Mods)
        {
            if (!serverById.TryGetValue(NormalizeCompatibilityId(clientMod.PackageId), out ModManifestEntry? serverMod))
            {
                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.UnexpectedMod,
                    $"Unexpected unapproved mod: {clientMod.PackageId}",
                    clientMod.PackageId));
                continue;
            }

            CompatibilityManifest partialServer = server with { Mods = new[] { serverMod } };
            CompatibilityManifest partialClient = client with { Mods = new[] { clientMod } };
            issues.AddRange(CompatibilityManifestComparer.Compare(partialServer, partialClient, options).Issues);
        }

        return new CompatibilityComparisonResult(issues);
    }

    private static void CompareSummaryScalar<T>(
        List<CompatibilityIssue> issues,
        T server,
        T client,
        CompatibilityIssueCode code,
        string message,
        string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(server, client))
        {
            issues.Add(new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject));
        }
    }

    private static void CompareSummarySequence(
        List<CompatibilityIssue> issues,
        IReadOnlyList<string> server,
        IReadOnlyList<string> client,
        CompatibilityIssueCode code,
        string message,
        string subject)
    {
        if (!server.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(client.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new CompatibilityIssue(CompatibilityIssueSeverity.Error, code, message, subject));
        }
    }

    private static void CompareSummaryDefSummaries(
        List<CompatibilityIssue> issues,
        IReadOnlyList<DefSummary> serverDefs,
        IReadOnlyList<DefSummary> clientDefs)
    {
        var serverByName = serverDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        var clientByName = clientDefs.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DefSummary> serverDefPair in serverByName)
        {
            if (!clientByName.TryGetValue(serverDefPair.Key, out DefSummary? clientDef)
                || serverDefPair.Value.Count != clientDef.Count
                || serverDefPair.Value.Hash != clientDef.Hash)
            {
                issues.Add(new CompatibilityIssue(
                    CompatibilityIssueSeverity.Error,
                    CompatibilityIssueCode.DefSummaryHashMismatch,
                    $"Def summary mismatch: {serverDefPair.Key}",
                    serverDefPair.Key));
            }
        }

        foreach (string name in clientByName.Keys.Except(serverByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new CompatibilityIssue(
                CompatibilityIssueSeverity.Error,
                CompatibilityIssueCode.DefSummaryUnexpected,
                $"Client has unapproved Def summary: {name}",
                name));
        }
    }

    private static string ComputeServerModHash(ModManifestEntry mod)
    {
        string stableBody = string.Join("\n", new[]
            {
                mod.LoadOrder.ToString(CultureInfo.InvariantCulture),
                mod.PackageId,
                mod.Name,
                mod.Source,
                mod.WorkshopId,
                mod.Role.ToString()
            })
            + "\n" + string.Join("\n", mod.Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.RelativePath + "|" + file.Size.ToString(CultureInfo.InvariantCulture) + "|" + file.Sha256 + "|" + file.Kind))
            + "\n" + string.Join("\n", mod.Configs
                .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(config => config.FileName + "|" + config.Sha256));
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(stableBody));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeCompatibilityId(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string SerializeCompatibilityManifest(CompatibilityManifest manifest, ClashOfRimNetworkState state)
    {
        CompatibilityManifest enriched = manifest with
        {
            ModConfigRules = state.ServerConfiguration.CompatibilityOptions.ModConfigRules
        };
        return JsonSerializer.Serialize(enriched, CompatibilityJsonOptions);
    }

    private static IReadOnlyList<CompatibilityIssueDto> ToCompatibilityIssueDtos(
        IReadOnlyList<CompatibilityIssue> issues)
    {
        return issues
            .Select(issue => new CompatibilityIssueDto(
                issue.Severity.ToString(),
                issue.Code.ToString(),
                issue.Message,
                issue.Subject))
            .ToList();
    }
}
