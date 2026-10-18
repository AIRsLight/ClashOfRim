using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class ColonyRelocation(ColonyLifecycleAccess access, SnapshotMutationCoordinator mutations,
    IColonySnapshotIndexStore snapshots, WorldConfigurationRegistry world, SnapshotColonyLocations locations,
    IWorldTileDistanceCalculator distances, RaidMarkerProjection raids, IAuthoritativeEventLedger ledger,
    ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline, AchievementAwards achievements,
    WorldConfigurationDelivery delivery, WorldMarkerDelivery markers, WorldConfigurationChanges changes, Func<ILogger> logger)
{
    internal ColonyRelocationResponse Preflight(PreflightColonyRelocationRequest request) => Execute(new(
        request.ProtocolVersion, request.UserId, request.ColonyId, request.CurrentSnapshotId, request.TargetTile, request.TargetTileLayerId, request.AuthToken, false));

    internal ColonyRelocationResponse Confirm(ConfirmColonyRelocationRequest request) => Execute(new(
        request.ProtocolVersion, request.UserId, request.ColonyId, request.RelocatedSnapshotId, request.TargetTile, request.TargetTileLayerId, request.AuthToken, true));

    private ColonyRelocationResponse Execute(Request request)
    {
        PlayerColonySiteDto? oldSite = null;
        WorldConfigurationDto? configuration = null;
        ColonyRelocationResponse Reject(ProtocolResponse failure) => new(failure, oldSite, null, delivery.Build(configuration), null);
        ColonyRelocationResponse RejectKey(string key, ProtocolErrorCode code = ProtocolErrorCode.ServerRejected) => Reject(ProtocolResponse.Reject(code, Format(key)));
        if (request.ProtocolVersion != ProtocolApiVersion.Current)
            return RejectKey("Protocol.IncompatibleVersion", ProtocolErrorCode.IncompatibleProtocolVersion);
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.SnapshotId) || request.Tile < 0 || request.Layer < 0)
            return RejectKey("ColonyRelocation.MissingRequest", ProtocolErrorCode.ValidationFailed);
        ProtocolResponse? Authorize() => access.Authorize(request.Token, request.UserId, request.ColonyId);
        if (Authorize() is { } invalid) return Reject(invalid);
        using var mutation = mutations.Enter(Authorize);
        if (mutation.Rejection is { } denied) return Reject(denied);
        configuration = world.Current;
        if (configuration is null) return RejectKey("WorldColonySites.WorldNotConfigured");
        LatestSnapshotRecord? latest = snapshots.GetLatest(request.UserId, request.ColonyId);
        if (latest is null) return RejectKey("ColonyRelocation.NoLatestSnapshot");
        if (latest.Identity.SnapshotId != request.SnapshotId)
            return RejectKey(request.Confirm ? "ColonyRelocation.RelocatedSnapshotNotLatest" : "ColonyRelocation.SnapshotNotLatest", ProtocolErrorCode.SnapshotMismatch);
        var ownSites = configuration.PlayerColonySites.Where(site => site.Tile >= 0 && site.UserId == request.UserId && site.ColonyId == request.ColonyId)
            .GroupBy(WorldColonySites.Key).Select(group => group.First()).ToArray();
        oldSite = ownSites.FirstOrDefault();
        if (!request.Confirm && ownSites.Length != 1) return RejectKey("ColonyRelocation.ExpectedExistingSingleSite");
        if (raids.HasParticipant(request.UserId, request.ColonyId)) return RejectKey("ColonyRelocation.BlockedByActiveRaid");
        if (oldSite is not null && HasPendingOldMapBoundDeliveryEvent(request.UserId, request.ColonyId, oldSite))
            return RejectKey("ColonyRelocation.BlockedByPendingTargetEvent");
        PlayerColonySiteDto target = new(request.UserId, request.ColonyId, null, null, request.Tile, request.ColonyId, tileLayerId: request.Layer);
        if (ColonySiteRules.TargetRejection(configuration, distances, configuration.PlayerColonySites, [target], request.UserId, request.ColonyId) is { } conflict)
            return Reject(conflict);
        if (!request.Confirm)
            return new(ProtocolResponse.Ok(Format("ColonyRelocation.PreflightAccepted")), oldSite, null, delivery.Build(configuration), null);

        IReadOnlyList<SnapshotColonyAnchor> anchors = locations.Extract(latest.Index);
        if (anchors.Count == 1 && (anchors[0].Tile != request.Tile || anchors[0].TileLayerId != request.Layer))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("ColonyRelocation.TargetTileMismatch",
                ("EXPECTED", TileRef(request.Tile, request.Layer)), ("ACTUAL", TileRef(anchors[0].Tile, anchors[0].TileLayerId)))));
        var targets = anchors.Where(anchor => anchor.Tile == request.Tile && anchor.TileLayerId == request.Layer).ToArray();
        if (targets.Length != 1) return RejectKey("ColonyRelocation.ExpectedSingleAnchor");
        var anchor = targets[0];
        var newSite = new PlayerColonySiteDto(request.UserId, request.ColonyId, anchor.WorldObjectId, anchor.MapUniqueId, anchor.Tile,
            string.IsNullOrWhiteSpace(anchor.Label) ? request.ColonyId : anchor.Label, oldSite?.FactionName, oldSite?.Appearance, anchor.TileLayerId);
        var extensions = plugins.ActiveWorldConfigurationExtensions(baseline.Current);
        var incoming = extensions.BuildExtensionsFromAcceptedSnapshot(new(latest, request.UserId, request.ColonyId, configuration.Extensions));
        WorldSessionState session = world.RegisterPlayerColonySites(request.UserId, request.ColonyId, [newSite], incoming, extensions);
        changes.Signal(request.UserId);
        logger().LogInformation(
            "殖民地搬迁完成：user={UserId} colony={ColonyId} snapshot={SnapshotId} tile {OldTile}->{NewTile} map={MapUniqueId} worldObject={WorldObjectId}",
            request.UserId, request.ColonyId, request.SnapshotId, oldSite is null ? null : TileRef(oldSite.Tile, oldSite.TileLayerId),
            TileRef(newSite.Tile, newSite.TileLayerId), newSite.MapUniqueId, newSite.WorldObjectId);
        var now = DateTimeOffset.UtcNow;
        achievements.RecordRelocation(request.UserId, request.ColonyId, request.SnapshotId, now);
        return new(ProtocolResponse.Ok(Format("ColonyRelocation.Confirmed")), oldSite, newSite, delivery.Build(session.WorldConfiguration),
            ProtocolDtoMapper.ToDto(markers.Build(request.UserId, request.ColonyId, now)));
    }

    private bool HasPendingOldMapBoundDeliveryEvent(
        string userId,
        string? colonyId,
        PlayerColonySiteDto oldSite)
    {
        return ledger.ListForUser(userId)
            .Any(ledgerEvent =>
                IsRelocationBlockingEventForTarget(ledgerEvent, userId, colonyId)
                && IsActiveRelocationBlockingEvent(ledgerEvent.Status)
                && IsTargetContextBoundToColonySite(ledgerEvent.TargetContext, oldSite));
    }

    private static bool IsRelocationBlockingEventForTarget(
        AuthoritativeEvent ledgerEvent,
        string userId,
        string? colonyId)
    {
        if (!RaidMarkerProjection.MatchesParty(ledgerEvent.Target, userId, colonyId))
        {
            return false;
        }

        return ledgerEvent.Type switch
        {
            ServerEventType.ItemDelivery => true,
            ServerEventType.SupportPawn => true,
            ServerEventType.Trade => ledgerEvent.Payload is TradeEventPayload tradePayload
                && tradePayload.Stage is not TradeStage.MarketOrder
                && tradePayload.Stage is not TradeStage.AcceptedMemo,
            _ => false
        };
    }

    private static bool IsActiveRelocationBlockingEvent(ServerEventStatus status)
    {
        return status is ServerEventStatus.Recorded
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.DeliveredToClient
            or ServerEventStatus.Conflict;
    }

    private static bool IsTargetContextBoundToColonySite(EventTargetContext? targetContext, PlayerColonySiteDto site)
    {
        if (targetContext is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(site.WorldObjectId)
            && !string.IsNullOrWhiteSpace(targetContext.WorldObjectId)
            && string.Equals(targetContext.WorldObjectId, site.WorldObjectId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(site.MapUniqueId)
            && !string.IsNullOrWhiteSpace(targetContext.MapUniqueId)
            && string.Equals(targetContext.MapUniqueId, site.MapUniqueId, StringComparison.Ordinal))
        {
            return true;
        }

        return site.Tile >= 0 && targetContext.Tile == site.Tile;
    }

    private static string TileRef(int tile, int layer) => tile.ToString(CultureInfo.InvariantCulture) + "," + layer.ToString(CultureInfo.InvariantCulture);

    private sealed record Request(string ProtocolVersion, string UserId, string ColonyId, string SnapshotId, int Tile, int Layer, string? Token, bool Confirm);
}
