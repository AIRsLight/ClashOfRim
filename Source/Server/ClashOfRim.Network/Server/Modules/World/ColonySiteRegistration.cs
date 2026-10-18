using System.Globalization;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.World.ColonySiteRules;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class ColonySiteRegistration(ColonyLifecycleAccess access,
    SnapshotMutationCoordinator mutations, WorldConfigurationRegistry world, CompatibilityBaselineRegistry baseline,
    ServerPluginRegistry plugins, IWorldTileDistanceCalculator distances, PlayerSessionActivity activity,
    WorldConfigurationDelivery delivery, WorldMarkerDelivery markers, WorldConfigurationChanges changes, Func<ILogger> logger)
{
    internal RegisterPlayerColonySitesResponse Register(RegisterPlayerColonySitesRequest request)
    {
        RegisterPlayerColonySitesResponse Reject(ProtocolResponse failure, bool includeWorld = false)
            => new(failure, 0, includeWorld ? delivery.Build(world.Current) : null, null);
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, Format("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldColonySites.MissingIdentity")));
        ProtocolResponse? Authorize() => access.Authorize(request.AuthToken, request.UserId, request.ColonyId);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);
        var initialWorld = world.Current;
        if (initialWorld is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldColonySites.WorldNotConfigured")));
        if (request.Sites.Any(site => site is null))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldColonySites.EmptySites")), true);
        var sites = NormalizePlayerColonySites(request.UserId, request.ColonyId, request.Sites);
        if (sites.Count > 1)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldColonySites.SingleColonyOnly")), true);

        // Only the world identity and active plugin selection feed extension normalization.
        // Current occupation and geometry are read again after recovery, under the mutation gate.
        var compatibility = baseline.Current;
        var extensions = plugins.ActiveWorldConfigurationExtensions(compatibility);
        var incoming = extensions.NormalizeSubmittedExtensions(
            new(request.UserId, request.ColonyId, initialWorld.WorldConfigurationId), request.Extensions);
        if (sites.Count == 0 && incoming.Count == 0)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldColonySites.EmptySites")), true);
        ProtocolResponse? AuthorizePrepared()
        {
            if (Authorize() is { } failure) return failure;
            if (!ReferenceEquals(compatibility, baseline.Current))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldConfiguration.CompatibilityChanged"));
            return string.Equals(world.Current?.WorldConfigurationId, initialWorld.WorldConfigurationId, StringComparison.Ordinal) ? null
                : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldColonySites.WorldChanged"));
        }
        using var mutation = mutations.Enter(AuthorizePrepared);
        if (mutation.Rejection is { } denied) return Reject(denied);
        var current = world.Current!;
        var occupied = WorldColonySites.Current(current);
        if (FindExistingUserColonyConflict(occupied, sites, request.UserId, request.ColonyId) is { } otherColony)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, otherColony), true);
        if (FindExistingSameColonySiteOnDifferentTile(occupied, sites, request.UserId, request.ColonyId) is { } relocated)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("WorldColonySites.RelocationRequiresConfirmation",
                ("OLD_TILE", relocated.Tile.ToString(CultureInfo.InvariantCulture)), ("NEW_TILE", sites[0].Tile.ToString(CultureInfo.InvariantCulture)))), true);
        if (TargetRejection(current, distances, occupied, sites, request.UserId, request.ColonyId) is { } targetFailure)
            return Reject(targetFailure, true);

        WorldSessionState session = world.RegisterPlayerColonySites(request.UserId, request.ColonyId, sites, incoming, extensions);
        activity.Record(request.UserId, request.ColonyId, currentSnapshotId: null, DateTimeOffset.UtcNow);
        var delivered = delivery.Build(session.WorldConfiguration);
        var worldMarkers = markers.Build(request.UserId, request.ColonyId, DateTimeOffset.UtcNow);
        if (!request.SuppressWorldConfigurationNotification) changes.Signal(request.UserId);
        logger().LogInformation(
            "殖民地地块登记完成：user={UserId} colony={ColonyId} sites={SiteCount} extensions={ExtensionCount} tiles={Tiles} suppressSignal={SuppressSignal}",
            request.UserId, request.ColonyId, sites.Count, incoming.Count,
            string.Join(",", sites.Select(site => site.Tile.ToString(CultureInfo.InvariantCulture))), request.SuppressWorldConfigurationNotification);
        return new(ProtocolResponse.Ok(Format(sites.Count > 0 ? "WorldColonySites.Registered" : "WorldColonySites.WorldExtensionsRegistered")),
            sites.Count, delivered, ProtocolDtoMapper.ToDto(worldMarkers));
    }
}
