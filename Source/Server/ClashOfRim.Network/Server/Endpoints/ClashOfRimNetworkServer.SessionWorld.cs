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
    private static readonly TimeSpan SessionStreamWaitTimeout = TimeSpan.FromSeconds(5);

    private static WorldConfigurationExtensionService ActiveWorldConfigurationExtensions(ClashOfRimNetworkState state)
    {
        return state.Plugins.ActiveWorldConfigurationExtensions(state.CompatibilityBaseline.Current);
    }

    private static IResult PrepareWorldSession(PrepareWorldSessionRequest request, ClashOfRimNetworkState state)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new PrepareWorldSessionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                isAdministrator: false,
                worldConfigured: false,
                requiresInitialWorldConfiguration: false,
                administratorUserId: null,
                worldConfiguration: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Results.Ok(new PrepareWorldSessionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldSession.MissingIdentity")),
                isAdministrator: false,
                worldConfigured: false,
                requiresInitialWorldConfiguration: false,
                administratorUserId: null,
                worldConfiguration: null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);
        string requestedColonyId = string.IsNullOrWhiteSpace(request.ColonyId)
            ? CreateDefaultColonyId(request.UserId)
            : request.ColonyId!.Trim();
        string assignedColonyId = state.Players.ResolveActiveColonyId(request.UserId, requestedColonyId, nowUtc);
        WorldSessionState session = state.WorldConfiguration.Prepare(request.UserId);
        WorldConfigurationDto? deliveredConfiguration = BuildWorldConfigurationForDelivery(session.WorldConfiguration, state);
        LatestSnapshotRecord? latestSnapshot = state.SnapshotStore.GetLatest(request.UserId, assignedColonyId);
        bool hasRegisteredColonySite = HasRegisteredPlayerColonySite(deliveredConfiguration, request.UserId, assignedColonyId);
        if (latestSnapshot is null && !hasRegisteredColonySite)
        {
            string? snapshotBackedColonyId = FindExistingColonyIdForUser(
                state,
                deliveredConfiguration,
                request.UserId,
                requestedColonyId);
            if (!string.IsNullOrWhiteSpace(snapshotBackedColonyId)
                && !string.Equals(snapshotBackedColonyId, assignedColonyId, StringComparison.Ordinal))
            {
                assignedColonyId = snapshotBackedColonyId!;
                latestSnapshot = state.SnapshotStore.GetLatest(request.UserId, assignedColonyId);
                hasRegisteredColonySite = HasRegisteredPlayerColonySite(deliveredConfiguration, request.UserId, assignedColonyId);
            }
        }

        if (string.Equals(assignedColonyId, requestedColonyId, StringComparison.Ordinal)
            && latestSnapshot is null
            && !hasRegisteredColonySite
            && HasHistoricalColonyLedgerReference(state, request.UserId, requestedColonyId))
        {
            state.Players.MarkDeleted(request.UserId, requestedColonyId, currentSnapshotId: null, nowUtc);
            assignedColonyId = state.Players.ResolveActiveColonyId(request.UserId, requestedColonyId, nowUtc);
            latestSnapshot = state.SnapshotStore.GetLatest(request.UserId, assignedColonyId);
            hasRegisteredColonySite = HasRegisteredPlayerColonySite(deliveredConfiguration, request.UserId, assignedColonyId);
        }

        bool hasExistingColony = latestSnapshot is not null || hasRegisteredColonySite;
        CompatibilityHandshakeResult compatibility = ValidateCompatibilityHandshake(
            state,
            request.UserId,
            request.SteamAuthTicket,
            request.Password,
            request.CompatibilityManifestJson,
            request.CompatibilityManifestId,
            request.CompatibilityManifestSummaryJson,
            nowUtc);
        if (!compatibility.Result.Accepted)
        {
            return Results.Ok(new PrepareWorldSessionResponse(
                compatibility.Result,
                session.IsAdministrator,
                session.WorldConfigured,
                session.RequiresInitialWorldConfiguration,
                session.AdministratorUserId,
                worldConfiguration: null,
                hasExistingColony,
                latestSnapshot?.Identity.SnapshotId,
                serverCompatibilityManifestJson: compatibility.ServerCompatibilityManifestJson,
                compatibilityIssues: compatibility.CompatibilityIssues,
                canOverrideCompatibilityBaseline: compatibility.CanOverrideCompatibilityBaseline,
                assignedColonyId,
                requiresFullCompatibilityManifest: compatibility.RequiresFullCompatibilityManifest,
                requestedCompatibilityPackageIds: compatibility.RequestedCompatibilityPackageIds));
        }

        if (state.AdminControl.MaintenanceLoginLocked && !session.IsAdministrator)
        {
            string reason = string.IsNullOrWhiteSpace(state.AdminControl.MaintenanceReason)
                ? T("Login.MaintenanceLocked")
                : state.AdminControl.MaintenanceReason!;
            return Results.Ok(new PrepareWorldSessionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, reason),
                session.IsAdministrator,
                session.WorldConfigured,
                session.RequiresInitialWorldConfiguration,
                session.AdministratorUserId,
                worldConfiguration: null,
                hasExistingColony,
                latestSnapshot?.Identity.SnapshotId,
                serverCompatibilityManifestJson: compatibility.ServerCompatibilityManifestJson,
                compatibilityIssues: compatibility.CompatibilityIssues,
                canOverrideCompatibilityBaseline: compatibility.CanOverrideCompatibilityBaseline,
                assignedColonyId,
                requiresFullCompatibilityManifest: compatibility.RequiresFullCompatibilityManifest,
                requestedCompatibilityPackageIds: compatibility.RequestedCompatibilityPackageIds));
        }

        if (hasExistingColony)
        {
            AuthoritativeEvent? blockingRaid = FindDefenderLoginBlockingRaid(
                state,
                request.UserId,
                assignedColonyId);
            if (blockingRaid is not null)
            {
                return Results.Ok(new PrepareWorldSessionResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ServerRejected,
                        T("WorldSession.BlockedByDefenseRaid")),
                    session.IsAdministrator,
                    session.WorldConfigured,
                    session.RequiresInitialWorldConfiguration,
                    session.AdministratorUserId,
                    worldConfiguration: null,
                    hasExistingColony,
                    latestSnapshot?.Identity.SnapshotId,
                    assignedColonyId: assignedColonyId));
            }
        }

        RecordPlayerSeen(state, request.UserId, assignedColonyId, currentSnapshotId: null, nowUtc);
        RunClientLifecycleHooks(
            state,
            ClientLifecycleEvent.InitialWorldSessionPrepared(
                request.UserId,
                assignedColonyId,
                latestSnapshot?.Identity.SnapshotId,
                nowUtc));
        return Results.Ok(new PrepareWorldSessionResponse(
            ProtocolResponse.Ok(BuildWorldSessionMessage(session)),
            session.IsAdministrator,
            session.WorldConfigured,
            session.RequiresInitialWorldConfiguration,
            session.AdministratorUserId,
            worldConfiguration: null,
            hasExistingColony,
            latestSnapshot?.Identity.SnapshotId,
            compatibility.ServerCompatibilityManifestJson,
            compatibility.CompatibilityIssues,
            compatibility.CanOverrideCompatibilityBaseline,
            assignedColonyId,
            compatibility.RequiresFullCompatibilityManifest,
            compatibility.RequestedCompatibilityPackageIds));
    }

    private static IResult SubmitWorldConfiguration(
        SubmitWorldConfigurationRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new SubmitWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                isAdministrator: false,
                worldConfigured: false,
                administratorUserId: null,
                worldConfiguration: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || request.Configuration is null)
        {
            return Results.Ok(new SubmitWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldConfiguration.MissingRequest")),
                isAdministrator: false,
                worldConfigured: false,
                administratorUserId: null,
                worldConfiguration: null));
        }

        WorldSessionState before = state.WorldConfiguration.Prepare(request.UserId);
        if (!before.IsAdministrator)
        {
            return Results.Ok(new SubmitWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("WorldConfiguration.AdminOnly")),
                isAdministrator: false,
                before.WorldConfigured,
                before.AdministratorUserId,
                BuildWorldConfigurationForDelivery(before.WorldConfiguration, state)));
        }

        WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
        WorldConfigurationDto configuration = NormalizeWorldConfiguration(
            request.Configuration,
            request.UserId,
            request.ColonyId,
            activeWorldExtensions);
        if (!HasUsableWorldGenerationBaseline(configuration, out string baselineFailureReason))
        {
            return Results.Ok(new SubmitWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldConfiguration.InvalidBaseline", ("REASON", baselineFailureReason))),
                before.IsAdministrator,
                before.WorldConfigured,
                before.AdministratorUserId,
                BuildWorldConfigurationForDelivery(before.WorldConfiguration, state)));
        }

        WorldSessionState after = state.WorldConfiguration.Submit(request.UserId, configuration, activeWorldExtensions);
        RecordPlayerSeen(state, request.UserId, request.ColonyId, currentSnapshotId: null, DateTimeOffset.UtcNow);
        SignalWorldConfigurationChanged(state, request.UserId);
        RuntimeLogger(loggerFactory).LogInformation(
            "世界基线已提交：user={UserId} colony={ColonyId} administrator={AdministratorUserId} configured={WorldConfigured} playerSites={PlayerSiteCount}",
            request.UserId,
            request.ColonyId,
            after.AdministratorUserId,
            after.WorldConfigured,
            after.WorldConfiguration?.PlayerColonySites.Count ?? 0);
        return Results.Ok(new SubmitWorldConfigurationResponse(
            ProtocolResponse.Ok(T("WorldConfiguration.Registered")),
            after.IsAdministrator,
            after.WorldConfigured,
            after.AdministratorUserId,
            BuildWorldConfigurationForDelivery(after.WorldConfiguration, state)));
    }

    private static IResult SubmitWorldFeatureNames(
        SubmitWorldFeatureNamesRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new SubmitWorldFeatureNamesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                accepted: false,
                created: false,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.Language)
            || string.IsNullOrWhiteSpace(request.WorldConfigurationId)
            || request.Features.Count == 0)
        {
            return Results.Ok(new SubmitWorldFeatureNamesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldConfiguration.FeatureNamesMissingRequest")),
                accepted: false,
                created: false,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        AuthenticationValidationResult auth = ValidateAuthentication(
            state,
            request.UserId,
            request.SteamAuthTicket,
            request.Password,
            DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new SubmitWorldFeatureNamesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                accepted: false,
                created: false,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        WorldFeatureNameCatalogSubmitResult result = state.WorldConfiguration.SubmitWorldFeatureNames(
            request.UserId,
            request.ColonyId,
            request.Language,
            request.WorldConfigurationId,
            request.Features);
        WorldConfigurationDto? deliveredConfiguration = BuildWorldConfigurationForDelivery(result.WorldConfiguration, state);
        if (!result.Accepted)
        {
            return Results.Ok(new SubmitWorldFeatureNamesResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("WorldConfiguration.FeatureNamesRejected", ("REASON", result.Message))),
                accepted: false,
                created: false,
                worldConfiguration: deliveredConfiguration));
        }

        if (result.Created)
        {
            SignalWorldConfigurationChanged(state, request.UserId);
            RuntimeLogger(loggerFactory).LogInformation(
                "世界地区名目录已提交：user={UserId} colony={ColonyId} language={Language} features={FeatureCount}",
                request.UserId,
                request.ColonyId,
                request.Language,
                request.Features.Count);
        }

        return Results.Ok(new SubmitWorldFeatureNamesResponse(
            ProtocolResponse.Ok(result.Created
                ? T("WorldConfiguration.FeatureNamesRegistered")
                : T("WorldConfiguration.FeatureNamesAlreadyRegistered")),
            accepted: true,
            created: result.Created,
            worldConfiguration: deliveredConfiguration));
    }

    private static async Task<IResult> UploadWorldSubstrate(
        HttpRequest httpRequest,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        MultipartSnapshotRequest<UploadWorldSubstrateRequest>? multipart =
            await ReadMultipartSnapshotRequest<UploadWorldSubstrateRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new UploadWorldSubstrateResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldConfiguration.TileGeometryMissingRequest")),
                false, 0, 0, BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        UploadWorldSubstrateRequest request = multipart.Request;
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.WorldConfigurationId))
        {
            return Results.Ok(new UploadWorldSubstrateResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldConfiguration.TileGeometryMissingRequest")),
                false, 0, 0, BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        AuthenticationValidationResult auth = ValidateAuthentication(
            state, request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new UploadWorldSubstrateResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                false, 0, 0, BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state)));
        }

        WorldSubstrateStoreResult result = state.WorldConfiguration.StoreWorldSubstrate(
            auth.AuthenticatedUserId,
            request.ColonyId,
            request.WorldConfigurationId,
            multipart.Payload);
        WorldConfigurationDto? delivered = BuildWorldConfigurationForDelivery(result.WorldConfiguration, state);
        if (!result.Accepted)
        {
            return Results.Ok(new UploadWorldSubstrateResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                    T("WorldConfiguration.TileGeometryRejected", ("REASON", result.Message))),
                false,
                result.LayerCount,
                result.TileCenterCount,
                delivered));
        }

        RuntimeLogger(loggerFactory).LogInformation(
            "世界底图已提交：user={UserId} colony={ColonyId} world={WorldConfigurationId} bytes={PayloadBytes} layers={LayerCount} tileCenters={TileCenterCount}",
            auth.AuthenticatedUserId,
            request.ColonyId,
            request.WorldConfigurationId,
            multipart.Payload.Length,
            result.LayerCount,
            result.TileCenterCount);
        return Results.Ok(new UploadWorldSubstrateResponse(
            ProtocolResponse.Ok(T("WorldConfiguration.TileGeometryRegistered")),
            true,
            result.LayerCount,
            result.TileCenterCount,
            delivered));
    }

    private static IResult DownloadWorldSubstrate(
        DownloadWorldSubstrateRequest request,
        ClashOfRimNetworkState state)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.WorldConfigurationId))
        {
            return Results.BadRequest();
        }

        AuthenticationValidationResult auth = ValidateAuthentication(
            state, request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Unauthorized();
        }

        return state.WorldConfiguration.TryGetWorldSubstrate(request.WorldConfigurationId, out byte[]? payload)
            && payload is not null
            ? Results.File(payload, "application/octet-stream", "world-substrate.cors.gz")
            : Results.NotFound();
    }

    private static IResult GetWorldConfiguration(GetWorldConfigurationRequest request, ClashOfRimNetworkState state)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new GetWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                worldConfiguration: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Results.Ok(new GetWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldSession.MissingIdentity")),
                worldConfiguration: null));
        }

        AuthenticationValidationResult auth = ValidateAuthentication(state, request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new GetWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                worldConfiguration: null));
        }

        WorldConfigurationDto? configuration = BuildWorldConfigurationForDelivery(
            state.WorldConfiguration.Current,
            state,
            request.IncludeGenerationBaseline,
            request.IncludePlayerColonySites,
            request.IncludeWorldExtensions);
        if (configuration is null)
        {
            return Results.Ok(new GetWorldConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("WorldSession.WaitingForAdmin")),
                worldConfiguration: null));
        }

        return Results.Ok(new GetWorldConfigurationResponse(
            ProtocolResponse.Ok(T("Compatibility.Validated")),
            configuration));
    }

    private static IResult RegisterPlayerColonySites(
        RegisterPlayerColonySitesRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                acceptedCount: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldColonySites.MissingIdentity")),
                acceptedCount: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        WorldConfigurationDto? currentConfiguration = BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state);
        if (currentConfiguration is null)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("WorldColonySites.WorldNotConfigured")),
                acceptedCount: 0,
                worldConfiguration: null,
                worldMapMarkers: null));
        }

        IReadOnlyList<PlayerColonySiteDto> normalizedSites = NormalizePlayerColonySites(
            request.UserId,
            request.ColonyId,
            request.Sites);
        string worldConfigurationId = state.WorldConfiguration.Current?.WorldConfigurationId ?? $"world:{Guid.NewGuid():N}";
        WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
        IReadOnlyList<WorldConfigurationExtensionDto> incomingExtensions =
            activeWorldExtensions.NormalizeSubmittedExtensions(
                new WorldConfigurationExtensionContext(
                    request.UserId,
                    request.ColonyId,
                    worldConfigurationId),
                request.Extensions);
        if (normalizedSites.Count == 0 && incomingExtensions.Count == 0)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldColonySites.EmptySites")),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        if (normalizedSites.Count > 1)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("WorldColonySites.SingleColonyOnly")),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        string? existingUserColonyConflict = FindExistingUserColonyConflict(
            currentConfiguration.PlayerColonySites,
            normalizedSites,
            request.UserId,
            request.ColonyId);
        if (existingUserColonyConflict is not null)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, existingUserColonyConflict),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        if (FindExistingSameColonySiteOnDifferentTile(
                currentConfiguration.PlayerColonySites,
                normalizedSites,
                request.UserId,
                request.ColonyId) is PlayerColonySiteDto existingRelocatedSite)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "WorldColonySites.RelocationRequiresConfirmation",
                        ("OLD_TILE", existingRelocatedSite.Tile.ToString(CultureInfo.InvariantCulture)),
                        ("NEW_TILE", normalizedSites[0].Tile.ToString(CultureInfo.InvariantCulture)))),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        PlayerColonySiteDto? conflicting = FindSameTilePlayerColonyConflict(
            currentConfiguration.PlayerColonySites,
            normalizedSites,
            request.UserId,
            request.ColonyId);
        if (conflicting is not null)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "WorldColonySites.TileOccupied",
                        ("TILE", conflicting.Tile.ToString(CultureInfo.InvariantCulture)),
                        ("OWNER", conflicting.UserId + "/" + conflicting.ColonyId))),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        PlayerColonySiteDto? nearbyConflict = FindNearbyPlayerColonyConflict(
            state,
            currentConfiguration.PlayerColonySites,
            normalizedSites,
            request.UserId,
            request.ColonyId);
        if (nearbyConflict is not null)
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "WorldColonySites.TileTooClose",
                        ("TILE", nearbyConflict.Tile.ToString(CultureInfo.InvariantCulture)),
                        ("OWNER", nearbyConflict.UserId + "/" + nearbyConflict.ColonyId))),
                acceptedCount: 0,
                worldConfiguration: currentConfiguration,
                worldMapMarkers: null));
        }

        WorldSessionState session = state.WorldConfiguration.RegisterPlayerColonySites(
            request.UserId,
            request.ColonyId,
            normalizedSites,
            incomingExtensions,
            activeWorldExtensions);
        RecordPlayerSeen(state, request.UserId, request.ColonyId, currentSnapshotId: null, DateTimeOffset.UtcNow);
        WorldConfigurationDto? deliveredConfiguration = BuildWorldConfigurationForDelivery(session.WorldConfiguration, state);
        WorldMapMarkerDelivery markers = BuildWorldMapMarkerDelivery(request.UserId, request.ColonyId, state, DateTimeOffset.UtcNow);
        string message = normalizedSites.Count > 0 ? T("WorldColonySites.Registered") : T("WorldColonySites.WorldExtensionsRegistered");
        if (!request.SuppressWorldConfigurationNotification)
        {
            SignalWorldConfigurationChanged(state, request.UserId);
        }

        RuntimeLogger(loggerFactory).LogInformation(
            "殖民地地块登记完成：user={UserId} colony={ColonyId} sites={SiteCount} extensions={ExtensionCount} tiles={Tiles} suppressSignal={SuppressSignal}",
            request.UserId,
            request.ColonyId,
            normalizedSites.Count,
            incomingExtensions.Count,
            string.Join(",", normalizedSites.Select(site => site.Tile.ToString(CultureInfo.InvariantCulture))),
            request.SuppressWorldConfigurationNotification);
        return Results.Ok(new RegisterPlayerColonySitesResponse(
            ProtocolResponse.Ok(message),
            normalizedSites.Count,
            deliveredConfiguration,
            ProtocolDtoMapper.ToDto(markers)));
    }

    private static IResult PreflightColonyRelocation(PreflightColonyRelocationRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!ValidateColonyRelocationRequest(
                state,
                request.ProtocolVersion,
                request.UserId,
                request.ColonyId,
                request.CurrentSnapshotId,
                request.TargetTile,
                request.TargetTileLayerId,
                request.AuthToken,
                nowUtc,
                out PlayerColonySiteDto? oldSite,
                out ProtocolResponse? rejection))
        {
            return Results.Ok(new ColonyRelocationResponse(
                rejection!,
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        return Results.Ok(new ColonyRelocationResponse(
            ProtocolResponse.Ok(T("ColonyRelocation.PreflightAccepted")),
            oldSite,
            newSite: null,
            BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
            worldMapMarkers: null));
    }

    private static IResult ConfirmColonyRelocation(
        ConfirmColonyRelocationRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!ValidateColonyRelocationConfirmationRequest(
                state,
                request.ProtocolVersion,
                request.UserId,
                request.ColonyId,
                request.RelocatedSnapshotId,
                request.TargetTile,
                request.TargetTileLayerId,
                request.AuthToken,
                nowUtc,
                out PlayerColonySiteDto? oldSite,
                out ProtocolResponse? rejection))
        {
            return Results.Ok(new ColonyRelocationResponse(
                rejection!,
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(request.UserId, request.ColonyId);
        if (latest is null
            || !string.Equals(latest.Identity.SnapshotId, request.RelocatedSnapshotId, StringComparison.Ordinal))
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("ColonyRelocation.RelocatedSnapshotNotLatest")),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        int targetTileLayerId = Math.Max(0, request.TargetTileLayerId);
        IReadOnlyList<SnapshotColonyAnchor> anchors = ExtractSnapshotColonyAnchors(state, latest.Index);
        if (anchors.Count == 1
            && (anchors[0].Tile != request.TargetTile || anchors[0].TileLayerId != targetTileLayerId))
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "ColonyRelocation.TargetTileMismatch",
                        ("EXPECTED", FormatTileRef(request.TargetTile, targetTileLayerId)),
                        ("ACTUAL", FormatTileRef(anchors[0].Tile, anchors[0].TileLayerId)))),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        IReadOnlyList<SnapshotColonyAnchor> targetAnchors = anchors
            .Where(anchor => anchor.Tile == request.TargetTile && anchor.TileLayerId == targetTileLayerId)
            .ToList();
        if (targetAnchors.Count != 1)
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.ExpectedSingleAnchor")),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        SnapshotColonyAnchor anchor = targetAnchors[0];

        PlayerColonySiteDto newSite = new(
            request.UserId,
            request.ColonyId,
            anchor.WorldObjectId,
            anchor.MapUniqueId,
            anchor.Tile,
            string.IsNullOrWhiteSpace(anchor.Label) ? request.ColonyId : anchor.Label,
            oldSite?.FactionName,
            oldSite?.Appearance,
            anchor.TileLayerId);
        IReadOnlyList<PlayerColonySiteDto> normalizedSites = NormalizePlayerColonySites(
            request.UserId,
            request.ColonyId,
            new[] { newSite });
        if (normalizedSites.Count != 1)
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.ExpectedSingleAnchor")),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        PlayerColonySiteDto? tileConflict = FindSameTilePlayerColonyConflict(
            state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>(),
            normalizedSites,
            request.UserId,
            request.ColonyId);
        if (tileConflict is not null)
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "WorldColonySites.TileOccupied",
                        ("TILE", tileConflict.Tile.ToString(CultureInfo.InvariantCulture)),
                        ("OWNER", tileConflict.UserId + "/" + tileConflict.ColonyId))),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        PlayerColonySiteDto? nearbyConflict = FindNearbyPlayerColonyConflict(
            state,
            state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>(),
            normalizedSites,
            request.UserId,
            request.ColonyId);
        if (nearbyConflict is not null)
        {
            return Results.Ok(new ColonyRelocationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T(
                        "WorldColonySites.TileTooClose",
                        ("TILE", nearbyConflict.Tile.ToString(CultureInfo.InvariantCulture)),
                        ("OWNER", nearbyConflict.UserId + "/" + nearbyConflict.ColonyId))),
                oldSite,
                newSite: null,
                BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            activeWorldExtensions.BuildExtensionsFromAcceptedSnapshot(
                new WorldConfigurationExtensionSnapshotContext(
                    latest,
                    request.UserId,
                    request.ColonyId,
                    state.WorldConfiguration.Current?.Extensions ?? Array.Empty<WorldConfigurationExtensionDto>()));
        WorldSessionState session = state.WorldConfiguration.RegisterPlayerColonySites(
            request.UserId,
            request.ColonyId,
            normalizedSites,
            extensions,
            activeWorldExtensions);
        SignalWorldConfigurationChanged(state, request.UserId);
        RuntimeLogger(loggerFactory).LogInformation(
            "殖民地搬迁完成：user={UserId} colony={ColonyId} snapshot={SnapshotId} tile {OldTile}->{NewTile} map={MapUniqueId} worldObject={WorldObjectId}",
            request.UserId,
            request.ColonyId,
            request.RelocatedSnapshotId,
            oldSite is null ? null : FormatTileRef(oldSite.Tile, oldSite.TileLayerId),
            FormatTileRef(normalizedSites[0].Tile, normalizedSites[0].TileLayerId),
            normalizedSites[0].MapUniqueId,
            normalizedSites[0].WorldObjectId);
        RecordColonyRelocationAchievements(state, request.UserId, request.ColonyId, request.RelocatedSnapshotId, nowUtc);
        WorldMapMarkerDelivery markers = BuildWorldMapMarkerDelivery(request.UserId, request.ColonyId, state, nowUtc);
        return Results.Ok(new ColonyRelocationResponse(
            ProtocolResponse.Ok(T("ColonyRelocation.Confirmed")),
            oldSite,
            normalizedSites[0],
            BuildWorldConfigurationForDelivery(session.WorldConfiguration, state),
            ProtocolDtoMapper.ToDto(markers)));
    }

    private static bool ValidateColonyRelocationRequest(
        ClashOfRimNetworkState state,
        string protocolVersion,
        string userId,
        string colonyId,
        string currentSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string? authToken,
        DateTimeOffset nowUtc,
        out PlayerColonySiteDto? oldSite,
        out ProtocolResponse? rejection)
    {
        oldSite = FindRegisteredPlayerColonySite(state, userId, colonyId);
        rejection = null;

        if (!string.Equals(protocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(currentSnapshotId)
            || targetTile < 0
            || targetTileLayerId < 0)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("ColonyRelocation.MissingRequest"));
            return false;
        }

        if (!IsAuthorizedForColony(
                state,
                authToken,
                userId,
                colonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure);
            return false;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(userId, colonyId);
        if (latest is null)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.NoLatestSnapshot"));
            return false;
        }

        if (!string.Equals(latest.Identity.SnapshotId, currentSnapshotId, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("ColonyRelocation.SnapshotNotLatest"));
            return false;
        }

        List<PlayerColonySiteDto> userSites = (state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>())
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)
                && site.Tile >= 0)
            .GroupBy(ColonySiteKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (userSites.Count != 1)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.ExpectedExistingSingleSite"));
            return false;
        }

        oldSite = userSites[0];
        PlayerColonySiteDto requestedSite = new(
            userId,
            colonyId,
            null,
            null,
            targetTile,
            colonyId,
            tileLayerId: targetTileLayerId);
        PlayerColonySiteDto? tileConflict = FindSameTilePlayerColonyConflict(
            state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>(),
            new[] { requestedSite },
            userId,
            colonyId);
        if (tileConflict is not null)
        {
            rejection = ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T(
                    "WorldColonySites.TileOccupied",
                    ("TILE", tileConflict.Tile.ToString(CultureInfo.InvariantCulture)),
                    ("OWNER", tileConflict.UserId + "/" + tileConflict.ColonyId)));
            return false;
        }

        PlayerColonySiteDto? nearbyConflict = FindNearbyPlayerColonyConflict(
            state,
            state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>(),
            new[] { requestedSite },
            userId,
            colonyId);
        if (nearbyConflict is not null)
        {
            rejection = ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T(
                    "WorldColonySites.TileTooClose",
                    ("TILE", nearbyConflict.Tile.ToString(CultureInfo.InvariantCulture)),
                    ("OWNER", nearbyConflict.UserId + "/" + nearbyConflict.ColonyId)));
            return false;
        }

        if (HasActiveRaidForParticipant(state, userId, colonyId))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.BlockedByActiveRaid"));
            return false;
        }

        if (HasPendingOldMapBoundDeliveryEvent(state, userId, colonyId, oldSite))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.BlockedByPendingTargetEvent"));
            return false;
        }

        return true;
    }

    private static bool ValidateColonyRelocationConfirmationRequest(
        ClashOfRimNetworkState state,
        string protocolVersion,
        string userId,
        string colonyId,
        string relocatedSnapshotId,
        int targetTile,
        int targetTileLayerId,
        string? authToken,
        DateTimeOffset nowUtc,
        out PlayerColonySiteDto? oldSite,
        out ProtocolResponse? rejection)
    {
        oldSite = FindRegisteredPlayerColonySite(state, userId, colonyId);
        rejection = null;

        if (!string.Equals(protocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(relocatedSnapshotId)
            || targetTile < 0
            || targetTileLayerId < 0)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("ColonyRelocation.MissingRequest"));
            return false;
        }

        if (!IsAuthorizedForColony(
                state,
                authToken,
                userId,
                colonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure);
            return false;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(userId, colonyId);
        if (latest is null)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.NoLatestSnapshot"));
            return false;
        }

        if (!string.Equals(latest.Identity.SnapshotId, relocatedSnapshotId, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("ColonyRelocation.RelocatedSnapshotNotLatest"));
            return false;
        }

        if (HasActiveRaidForParticipant(state, userId, colonyId))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.BlockedByActiveRaid"));
            return false;
        }

        if (oldSite is not null && HasPendingOldMapBoundDeliveryEvent(state, userId, colonyId, oldSite))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("ColonyRelocation.BlockedByPendingTargetEvent"));
            return false;
        }

        return true;
    }

    private static string FormatTileRef(int tile, int tileLayerId)
    {
        return tile.ToString(CultureInfo.InvariantCulture)
            + ","
            + Math.Max(0, tileLayerId).ToString(CultureInfo.InvariantCulture);
    }

    private static PlayerColonySiteDto? FindRegisteredPlayerColonySite(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        return (state.WorldConfiguration.Current?.PlayerColonySites ?? Array.Empty<PlayerColonySiteDto>())
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)
                && site.Tile >= 0)
            .GroupBy(ColonySiteKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .FirstOrDefault();
    }

    private static int CreateWorldTileFloatLayerIncreaseNotifications(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string snapshotId,
        IReadOnlyList<WorldTileFloatLayerIncrease> increases,
        DateTimeOffset nowUtc)
    {
        if (increases.Count == 0 || state.WorldConfiguration.Current is not WorldConfigurationDto configuration)
        {
            return 0;
        }

        WorldTileGeometryDistanceSource? geometry = BuildWorldTileGeometryDistanceSource(configuration);
        int surfaceLayerId = ResolveSurfaceLayerId(configuration);
        EventParty actor = new(userId, colonyId, snapshotId);
        string actorLabel = ResolvePlayerDisplayName(state, userId, colonyId);
        var notificationContext = new WorldTileFloatLayerNotificationContext(actorLabel);
        int notified = 0;
        foreach (WorldTileFloatLayerIncrease increase in increases)
        {
            WorldTileFloatLayerIncreaseNotification? notification =
                ActiveWorldConfigurationExtensions(state).BuildIncreaseNotification(notificationContext, increase);
            if (notification is null || notification.RadiusTiles < 0)
            {
                continue;
            }

            foreach (PlayerColonySiteDto site in configuration.PlayerColonySites)
            {
                if (site.Tile < 0
                    || string.Equals(site.UserId, userId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(site.UserId)
                    || string.IsNullOrWhiteSpace(site.ColonyId))
                {
                    continue;
                }

                int? distance = state.WorldTileDistanceCalculator.TryCalculateDistance(
                    geometry,
                    new WorldTileRef(increase.Tile, surfaceLayerId),
                    new WorldTileRef(site.Tile, surfaceLayerId),
                    crossLayerOverheadDistanceTiles: 0);
                if (distance is null || distance > notification.RadiusTiles)
                {
                    continue;
                }

                string notificationId = $"{notification.Kind}:{userId}:{colonyId}:{snapshotId}:{increase.LayerId}:{increase.Tile}:{site.UserId}:{site.ColonyId}";
                AuthoritativeEvent worldLayerNotification = AuthoritativeEventFactory.Create(
                    ServerEventType.ServerNotification,
                    actor,
                    new EventParty(site.UserId, site.ColonyId),
                    notificationId,
                    state.OnlinePresence.IsUserOnline(site.UserId),
                    new ServerNotificationEventPayload(
                        notificationId,
                        notification.Title,
                        notification.Message,
                        notification.Severity,
                        FromAdministrator: false),
                    nowUtc,
                    new EventTargetContext(site.WorldObjectId, site.MapUniqueId, site.Tile, EventLandingMode.MapEdge));
                LogEventAppend(state, state.Ledger.Append(worldLayerNotification), "world-layer-notification");
                state.EventNotifications.SignalUser(site.UserId);
                notified++;
            }
        }

        return notified;
    }

    private static int ResolveSurfaceLayerId(WorldConfigurationDto configuration)
    {
        WorldTileLayerGeometryDto? surface = configuration.TileGeometry?.Layers.FirstOrDefault(layer =>
            string.Equals(layer.LayerDefName, "Surface", StringComparison.OrdinalIgnoreCase));
        if (surface is not null)
        {
            return surface.LayerId;
        }

        return configuration.TileGeometry?.Layers.OrderBy(layer => layer.LayerId).FirstOrDefault()?.LayerId ?? 0;
    }

    private static string ResolvePlayerDisplayName(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        PlayerSessionRecord? player = state.Players.FindByUserId(userId);
        if (player is not null && !string.Equals(player.ColonyId, colonyId, StringComparison.Ordinal))
        {
            player = null;
        }

        return string.IsNullOrWhiteSpace(player?.DisplayName) ? userId : player!.DisplayName!;
    }

    private static IResult AbandonPlayerColony(
        AbandonPlayerColonyRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AbandonColony.MissingRequest")),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(request.UserId, request.ColonyId);
        if (!string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
            && latest is not null
            && !string.Equals(latest.Identity.SnapshotId, request.CurrentSnapshotId, StringComparison.Ordinal))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("AbandonColony.SnapshotNotLatest")),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state),
                worldMapMarkers: null));
        }

        AbandonedColonyCleanupResult cleanup = CleanupAbandonedColony(
            state,
            request.UserId,
            request.ColonyId,
            latest,
            nowUtc);

        WorldConfigurationDto? deliveredConfiguration = BuildWorldConfigurationForDelivery(
            cleanup.RemovedSites.Session.WorldConfiguration,
            state);
        WorldMapMarkerDelivery markers = BuildWorldMapMarkerDelivery(request.UserId, request.ColonyId, state, nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "殖民地废弃完成：user={UserId} colony={ColonyId} snapshotRemoved={SnapshotRemoved} removedSites={RemovedSites} removedEvents={RemovedEvents} removedRuntimeMarkers={RemovedRuntimeMarkers}",
            request.UserId,
            request.ColonyId,
            cleanup.SnapshotRemoved,
            cleanup.RemovedSites.RemovedCount,
            cleanup.RemovedEvents,
            cleanup.RemovedRuntimeMarkers);
        return Results.Ok(new AbandonPlayerColonyResponse(
            ProtocolResponse.Ok(T("AbandonColony.Completed")),
            cleanup.SnapshotRemoved ? 1 : 0,
            cleanup.RemovedSites.RemovedCount,
            cleanup.RemovedEvents,
            cleanup.RemovedRuntimeMarkers,
            deliveredConfiguration,
            ProtocolDtoMapper.ToDto(markers)));
    }

    private static AbandonedColonyCleanupResult CleanupAbandonedColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        LatestSnapshotRecord? latest,
        DateTimeOffset nowUtc)
    {
        string? abandonedSnapshotId = latest?.Identity.SnapshotId;
        bool snapshotRemoved = state.SnapshotStore.RemoveLatest(userId, colonyId);
        RemovePlayerColonySitesResult removedSites = state.WorldConfiguration.RemovePlayerColonySites(
            userId,
            colonyId,
            ActiveWorldConfigurationExtensions(state));
        int removedEvents = 0;
        int removedRuntimeMarkers = state.RuntimeWorldObjectMarkers.RemoveForOwner(userId, colonyId);
        state.DiplomacyRelations.RemoveForColony(userId, colonyId);
        state.BankLoans.RemoveForColony(userId, colonyId);
        state.Players.MarkDeleted(userId, colonyId, abandonedSnapshotId, nowUtc);
        AppendColonyTombstoneEvent(state, userId, colonyId, abandonedSnapshotId, nowUtc);
        state.AuthTokens.RevokeForColony(userId, colonyId);
        state.EventNotifications.SignalUser(userId);
        SignalWorldConfigurationChanged(state, userId);
        RunClientLifecycleHooks(
            state,
            ClientLifecycleEvent.ColonyAbandoned(
                userId,
                colonyId,
                abandonedSnapshotId,
                nowUtc));

        return new AbandonedColonyCleanupResult(
            snapshotRemoved,
            removedSites,
            removedEvents,
            removedRuntimeMarkers);
    }

    private sealed record AbandonedColonyCleanupResult(
        bool SnapshotRemoved,
        RemovePlayerColonySitesResult RemovedSites,
        int RemovedEvents,
        int RemovedRuntimeMarkers);
}
