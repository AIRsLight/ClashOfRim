using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;
using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeDelivery(IColonySnapshotIndexStore snapshots, OnlinePresenceRegistry presence,
    IAuthoritativeEventLedger ledger, ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility,
    WorldConfigurationRegistry world, IWorldTileDistanceCalculator distances, Func<ClashOfRimServerConfiguration> configuration)
{
    internal TradePostageQuoteDto BuildTradePostageQuote(
        AuthoritativeEvent tradeOrder,
        string viewerUserId,
        string viewerColonyId,
        string viewerSnapshotId)
    {
        if (tradeOrder.Payload is not TradeEventPayload payload
            || payload.FulfillmentMode is not TradeFulfillmentMode.ServerDropPod and not TradeFulfillmentMode.Unspecified)
        {
            return UnreachablePostage(Format("Trade.PostageDropPodDisabled"));
        }

        TradeDeliveryEndpoint ownerEndpoint = ResolveTradeDeliveryEndpoint(
            tradeOrder.Actor.UserId,
            tradeOrder.Actor.ColonyId,
            snapshotId: null,
            tradeOrder.TargetContext);
        if (!ownerEndpoint.Available)
        {
            return UnreachablePostage(Format("Trade.PostageOwnerUnavailable", ("STATUS", ownerEndpoint.Status)));
        }

        TradeDeliveryEndpoint viewerEndpoint = ResolveTradeDeliveryEndpoint(
            viewerUserId,
            viewerColonyId,
            viewerSnapshotId,
            preferredContext: null);
        if (!viewerEndpoint.Available)
        {
            return UnreachablePostage(Format("Trade.PostageReceiverUnavailable", ("STATUS", viewerEndpoint.Status)));
        }

        if (ownerEndpoint.TileRef is not WorldTileRef fromTile || viewerEndpoint.TileRef is not WorldTileRef toTile)
        {
            return UnreachablePostage(Format("Trade.PostageMissingTiles"));
        }

        ClashOfRimServerConfiguration policy = configuration();
        int? distance = fromTile == toTile
            ? 0
            : distances.TryCalculateDistance(
                BuildWorldTileGeometryDistanceSource(world.Current),
                fromTile,
                toTile,
                policy.TradePostageCrossLayerOverheadDistanceTiles);
        if (distance is null)
        {
            return UnreachablePostage(Format("Trade.PostageMissingGeometry"));
        }

        int postage = policy.TradePostageBaseSilver
            + Math.Max(0, distance.Value) * policy.TradePostageSilverPerTile;
        return new TradePostageQuoteDto(
            reachable: true,
            postageSilver: postage,
            distanceTiles: Math.Max(0, distance.Value),
            status: fromTile.LayerId == toTile.LayerId
                ? Format("Trade.PostageSurfaceDistance")
                : Format("Trade.PostageCrossLayerDistance"));
    }

    private static TradePostageQuoteDto UnreachablePostage(string status)
    {
        return new TradePostageQuoteDto(
            reachable: false,
            postageSilver: null,
            distanceTiles: null,
            status: status);
    }

    internal TradeDeliveryEndpoint ResolveTradeDeliveryEndpoint(
        string userId,
        string? colonyId,
        string? snapshotId,
        EventTargetContext? preferredContext,
        LatestSnapshotRecord? preparedSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            return TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointMissingIdentity"));
        }

        LatestSnapshotRecord? snapshot = preparedSnapshot ?? snapshots.GetLatest(userId, colonyId!);
        if (snapshot is null)
        {
            return TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointMissingSnapshot"));
        }

        if (!string.IsNullOrWhiteSpace(snapshotId)
            && !string.Equals(snapshot.Identity.SnapshotId, snapshotId, StringComparison.Ordinal))
        {
            return TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointSnapshotMismatch"));
        }

        MapSummary? map = ResolveEndpointMap(snapshot.Index, preferredContext);
        WorldObjectSummary? worldObject = ResolveEndpointWorldObject(snapshot.Index, map, preferredContext);
        if (worldObject is null)
        {
            return preferredContext?.Tile is int contextTile
                ? TradeDeliveryEndpoint.Surface(
                    new WorldTileRef(contextTile, 0),
                    preferredContext.WorldObjectId,
                    preferredContext.MapUniqueId,
                    Format("Trade.EndpointContextTile"))
                : TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointMissingWorldObject"));
        }

        string? worldObjectId = worldObject.UniqueLoadId ?? worldObject.Id ?? preferredContext?.WorldObjectId;
        string? mapUniqueId = map?.UniqueId ?? preferredContext?.MapUniqueId;
        if (TryParsePlanetTile(worldObject.Tile, out WorldTileRef tile))
        {
            return IsOrbitalWorldObject(worldObject)
                ? TradeDeliveryEndpoint.Orbital(tile, worldObjectId, mapUniqueId, Format("Trade.EndpointOrbital"))
                : TradeDeliveryEndpoint.Surface(tile, worldObjectId, mapUniqueId, Format("Trade.EndpointSurface"));
        }

        if (IsOrbitalWorldObject(worldObject))
        {
            return TradeDeliveryEndpoint.Orbital(null, worldObjectId, mapUniqueId, Format("Trade.EndpointOrbital"));
        }

        return string.IsNullOrWhiteSpace(worldObject.Tile)
            ? TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointWorldObjectMissingTile"))
            : TradeDeliveryEndpoint.Unavailable(Format("Trade.EndpointWorldObjectUnrecognizedTile"));
    }

    private static MapSummary? ResolveEndpointMap(SaveSnapshotIndex index, EventTargetContext? preferredContext)
    {
        if (!string.IsNullOrWhiteSpace(preferredContext?.MapUniqueId))
        {
            MapSummary? matched = index.Maps.FirstOrDefault(map =>
                string.Equals(map.UniqueId, preferredContext.MapUniqueId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }

        }

        if (!string.IsNullOrWhiteSpace(preferredContext?.WorldObjectId))
        {
            MapSummary? matched = index.Maps.FirstOrDefault(map =>
                string.Equals(map.ParentWorldObjectId, preferredContext.WorldObjectId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }

        }

        return index.Maps.FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.UniqueId));
    }

    private static WorldObjectSummary? ResolveEndpointWorldObject(
        SaveSnapshotIndex index,
        MapSummary? map,
        EventTargetContext? preferredContext)
    {
        string? worldObjectId = preferredContext?.WorldObjectId ?? map?.ParentWorldObjectId;
        if (!string.IsNullOrWhiteSpace(worldObjectId))
        {
            WorldObjectSummary? matched = index.WorldObjects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueLoadId, worldObjectId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, worldObjectId, StringComparison.Ordinal));
            if (matched is not null)
            {
                return matched;
            }

        }

        if (preferredContext?.Tile is int tile)
        {
            return index.WorldObjects.FirstOrDefault(candidate =>
                TryParsePlanetTile(candidate.Tile, out WorldTileRef candidateTile)
                && candidateTile.LayerId == 0
                && candidateTile.Tile == tile);
        }

        if (!string.IsNullOrWhiteSpace(map?.ParentWorldObjectId))
        {
            return index.WorldObjects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueLoadId, map.ParentWorldObjectId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, map.ParentWorldObjectId, StringComparison.Ordinal));
        }

        return null;
    }

    private bool IsOrbitalWorldObject(WorldObjectSummary worldObject)
    {
        if (TryParsePlanetTile(worldObject.Tile, out WorldTileRef tileRef) && tileRef.LayerId > 0)
        {
            return true;
        }

        return plugins.ActiveWorldObjectClassifiers(compatibility.Current).Any(classifier =>
            {
                try
                {
                    return classifier.IsOrbitalWorldObject(worldObject);
                }

                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "[ClashOfRim][ServerPlugin][Warning] WorldObjectClassifierFailed: "
                        + (worldObject.UniqueLoadId ?? worldObject.Id ?? "<unknown>")
                        + " "
                        + ex.GetType().Name
                        + " "
                        + ex.Message);
                    return false;
                }

            });
    }

    internal EventTargetContext? ResolveTradeTargetContext(
        string userId,
        string colonyId,
        string snapshotId,
        EventLandingMode landingMode,
        LatestSnapshotRecord? preparedSnapshot = null)
    {
        TradeDeliveryEndpoint endpoint = ResolveTradeDeliveryEndpoint(
            userId,
            colonyId,
            snapshotId,
            preferredContext: null, preparedSnapshot);
        if (!endpoint.Available && endpoint.WorldObjectId is null && endpoint.MapUniqueId is null)
        {
            return null;
        }

        return new EventTargetContext(
            endpoint.WorldObjectId,
            endpoint.MapUniqueId,
            endpoint.Tile,
            landingMode);
    }

    internal AuthoritativeEvent CreateTradeItemDeliveryEvent(
        string idempotencyKey, EventParty actor, EventParty target,
        IReadOnlyList<EventThingReference> things, ItemDeliveryPurpose purpose, EventTargetContext? targetContext, DateTimeOffset nowUtc)
    {
        return AuthoritativeEventFactory.CreateForLedger(ledger,
            ServerEventType.ItemDelivery,
            actor,
            target,
            idempotencyKey,
            presence.IsUserOnline(target.UserId),
            new ItemDeliveryEventPayload(things, Message: null, Purpose: purpose),
            nowUtc,
            targetContext);
    }

    internal sealed record TradeDeliveryEndpoint(
        bool Available,
        bool IsOrbital,
        WorldTileRef? TileRef,
        string? WorldObjectId,
        string? MapUniqueId,
        string Status)
    {
        public int? Tile => TileRef is WorldTileRef tileRef && tileRef.LayerId == 0 ? tileRef.Tile : null;

        public static TradeDeliveryEndpoint Unavailable(string status)
        {
            return new TradeDeliveryEndpoint(
                Available: false,
                IsOrbital: false,
                TileRef: null,
                WorldObjectId: null,
                MapUniqueId: null,
                Status: status);
        }

        public static TradeDeliveryEndpoint Surface(
            WorldTileRef tile,
            string? worldObjectId,
            string? mapUniqueId,
            string status)
        {
            return new TradeDeliveryEndpoint(
                Available: true,
                IsOrbital: false,
                TileRef: tile,
                WorldObjectId: worldObjectId,
                MapUniqueId: mapUniqueId,
                Status: status);
        }

        public static TradeDeliveryEndpoint Orbital(
            WorldTileRef? tile,
            string? worldObjectId,
            string? mapUniqueId,
            string status)
        {
            return new TradeDeliveryEndpoint(
                Available: true,
                IsOrbital: true,
                TileRef: tile,
                WorldObjectId: worldObjectId,
                MapUniqueId: mapUniqueId,
                Status: status);
        }

    }
}
