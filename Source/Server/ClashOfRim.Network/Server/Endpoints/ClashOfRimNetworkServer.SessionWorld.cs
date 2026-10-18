using static AIRsLight.ClashOfRim.Network.Modules.World.ColonySiteRules;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
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
    private static WorldConfigurationExtensionService ActiveWorldConfigurationExtensions(ClashOfRimNetworkState state)
    {
        return state.Plugins.ActiveWorldConfigurationExtensions(state.CompatibilityBaseline.Current);
    }

    private static IResult RegisterPlayerColonySites(
        RegisterPlayerColonySitesRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        using var mutation = state.EnterSnapshotMutation();
        RecoverSnapshotCommits(state);
        if (!string.IsNullOrWhiteSpace(request.UserId) && !string.IsNullOrWhiteSpace(request.ColonyId)
            && state.Players.IsDeleted(request.UserId, request.ColonyId))
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Auth.IdentityDenied")), 0,
                state.WorldDelivery.Build(state.WorldConfiguration.Current), null));
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                acceptedCount: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new RegisterPlayerColonySitesResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("WorldColonySites.MissingIdentity")),
                acceptedCount: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        WorldConfigurationDto? currentConfiguration = state.WorldDelivery.Build(state.WorldConfiguration.Current);
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
            state.WorldConfiguration.Current, state.WorldTileDistanceCalculator,
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
        state.PlayerActivity.Record(request.UserId, request.ColonyId, currentSnapshotId: null, DateTimeOffset.UtcNow);
        WorldConfigurationDto? deliveredConfiguration = state.WorldDelivery.Build(session.WorldConfiguration);
        WorldMapMarkerDelivery markers = state.WorldMarkers.Build(request.UserId, request.ColonyId, DateTimeOffset.UtcNow);
        string message = normalizedSites.Count > 0 ? T("WorldColonySites.Registered") : T("WorldColonySites.WorldExtensionsRegistered");
        if (!request.SuppressWorldConfigurationNotification)
        {
            state.WorldChanges.Signal(request.UserId);
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        return Results.Ok(new ColonyRelocationResponse(
            ProtocolResponse.Ok(T("ColonyRelocation.PreflightAccepted")),
            oldSite,
            newSite: null,
            state.WorldDelivery.Build(state.WorldConfiguration.Current),
            worldMapMarkers: null));
    }

    private static IResult ConfirmColonyRelocation(
        ConfirmColonyRelocationRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        using var mutation = state.EnterSnapshotMutation();
        RecoverSnapshotCommits(state);
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        int targetTileLayerId = Math.Max(0, request.TargetTileLayerId);
        IReadOnlyList<SnapshotColonyAnchor> anchors = state.ColonyLocations.Extract(latest.Index);
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        PlayerColonySiteDto? nearbyConflict = FindNearbyPlayerColonyConflict(
            state.WorldConfiguration.Current, state.WorldTileDistanceCalculator,
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
                state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
        state.WorldChanges.Signal(request.UserId);
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
        WorldMapMarkerDelivery markers = state.WorldMarkers.Build(request.UserId, request.ColonyId, nowUtc);
        return Results.Ok(new ColonyRelocationResponse(
            ProtocolResponse.Ok(T("ColonyRelocation.Confirmed")),
            oldSite,
            normalizedSites[0],
            state.WorldDelivery.Build(session.WorldConfiguration),
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
            .GroupBy(WorldColonySites.Key)
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
            state.WorldConfiguration.Current, state.WorldTileDistanceCalculator,
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

        if (state.RaidMarkers.HasParticipant(userId, colonyId))
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

        if (state.RaidMarkers.HasParticipant(userId, colonyId))
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
            .GroupBy(WorldColonySites.Key)
            .Select(group => group.First())
            .FirstOrDefault();
    }

    private static IReadOnlyList<AuthoritativeEvent> BuildWorldTileFloatLayerIncreaseNotifications(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string snapshotId,
        IReadOnlyList<WorldTileFloatLayerIncrease> increases,
        DateTimeOffset nowUtc)
    {
        if (increases.Count == 0 || state.WorldConfiguration.Current is not WorldConfigurationDto configuration)
        {
            return [];
        }

        WorldTileGeometryDistanceSource? geometry = BuildWorldTileGeometryDistanceSource(configuration);
        int surfaceLayerId = ResolveSurfaceLayerId(configuration);
        EventParty actor = new(userId, colonyId, snapshotId);
        string actorLabel = ResolvePlayerDisplayName(state, userId, colonyId);
        var notificationContext = new WorldTileFloatLayerNotificationContext(actorLabel);
        var notifications = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
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
                AuthoritativeEvent worldLayerNotification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
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
                notifications.TryAdd(notificationId, worldLayerNotification);
            }
        }

        return notifications.Values.ToArray();
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
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        // Reject unauthenticated requests before doing recovery work. Repeat this
        // check under the gate because a valid request may wait behind a deletion.
        if (!IsAuthorizedForColony(state, request.AuthToken, request.UserId, request.ColonyId,
                authorizationEventId: null, authorizationScope: null, nowUtc, out string earlyAuthFailure))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, earlyAuthFailure),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        AbandonedColonyCleanupResult cleanup;
        using (state.EnterSnapshotMutation())
        {
            RecoverSnapshotCommits(state);
            nowUtc = DateTimeOffset.UtcNow;
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
                    worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
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
                    worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                    worldMapMarkers: null));
            }

            cleanup = CleanupAbandonedColonyLocked(
                state,
                request.UserId,
                request.ColonyId,
                latest,
                nowUtc);
        }

        WorldConfigurationDto? deliveredConfiguration = state.WorldDelivery.Build(cleanup.RemovedSites.Session.WorldConfiguration);
        WorldMapMarkerDelivery markers = state.WorldMarkers.Build(request.UserId, request.ColonyId, nowUtc);
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
        DateTimeOffset nowUtc)
    {
        using (state.EnterSnapshotMutation())
        {
            RecoverSnapshotCommits(state);
            return CleanupAbandonedColonyLocked(state, userId, colonyId, state.SnapshotStore.GetLatest(userId, colonyId), nowUtc);
        }
    }

    private static AbandonedColonyCleanupResult CleanupAbandonedColonyLocked(
        ClashOfRimNetworkState state, string userId, string colonyId, LatestSnapshotRecord? latest, DateTimeOffset nowUtc)
    {
        if (state.SnapshotCommits.HasPending)
            throw new InvalidOperationException("Finish snapshot commits before beginning colony deletion.");
        LedgerBatch closure = BuildAbandonedColonyEventClosure(state.Ledger, userId, colonyId, out var notifyUsers);
        var existing = state.Ledger.FindByIdempotencyKey(ColonyTombstones.Key(userId, colonyId));
        if (existing is not null && latest is not null)
            throw new InvalidDataException("A deleted colony has a snapshot again; refusing to reuse its tombstone.");
        AuthoritativeEvent tombstone = existing ?? CreateColonyTombstoneEvent(userId, colonyId, latest?.Identity.SnapshotId, nowUtc);
        notifyUsers.Add(userId);
        var intent = new ColonyDeletionIntent(1, Guid.NewGuid().ToString("N"), userId, colonyId, latest?.Envelope,
            tombstone.CreatedAtUtc, closure with { Events = [tombstone] },
            closure.StatusChanges.Select(change => state.Ledger.Find(change.EventId)!).ToArray(), notifyUsers.ToArray(),
            state.Players.List().Select(player => player.UserId).Append(userId).Distinct(StringComparer.Ordinal).ToArray());
        intent.Preflight(state.SnapshotStore, state.Ledger);
        return state.ColonyDeletions.Commit(intent, pending => ApplyColonyDeletion(state, pending));
    }

    private static AbandonedColonyCleanupResult ApplyColonyDeletion(ClashOfRimNetworkState state, ColonyDeletionIntent intent)
    {
        intent.Preflight(state.SnapshotStore, state.Ledger);
        string userId = intent.UserId, colonyId = intent.ColonyId;
        bool snapshotRemoved = state.SnapshotStore.RemoveLatest(userId, colonyId);
        RemovePlayerColonySitesResult removedSites = state.WorldConfiguration.RemovePlayerColonySites(
            userId,
            colonyId,
            ActiveWorldConfigurationExtensions(state));
        int removedEvents = 0;
        int removedRuntimeMarkers = state.RuntimeWorldObjectMarkers.RemoveForOwner(userId, colonyId);
        state.DiplomacyRelations.RemoveForColony(userId, colonyId);
        state.BankLoans.RemoveForColony(userId, colonyId);
        state.Players.MarkDeleted(userId, colonyId, intent.Tombstone.AppliedSnapshotId, intent.DeletedAtUtc);
        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);
        state.Ledger.ApplyBatch(intent.Batch);
        // Deletion and login publication share the mutation gate. Retire only this
        // colony's connection; recovery must never disconnect its replacement.
        state.SessionConnections.EndUnderMutation(userId, state.LoginSessions.FindSessionIdForColony(userId, colonyId));
        state.AuthTokens.RevokeForColony(userId, colonyId);
        state.EventNotifications.SignalUsers(intent.NotifyUsers);
        state.WorldConfigurationNotifications.SignalUsers(intent.WorldNotifyUsers.Concat(state.Players.List().Select(player => player.UserId)));

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
