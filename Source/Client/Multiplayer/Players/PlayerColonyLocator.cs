using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Players;

internal sealed class PlayerColonyLocator
{
    private const float PlayerLocationCacheSeconds = 2f;
    private readonly Func<List<ModWorldMapMarkerDto>> readMarkers;
    private readonly Action<string> requestRefresh;
    private readonly Func<float> clock;
    private readonly Dictionary<string, PlayerLocationCacheEntry> playerLocationCache = new(StringComparer.Ordinal);
    private int playerLocationCachePlayersVersion = -1;
    private World? cachedWorld;

    internal PlayerColonyLocator(Func<List<ModWorldMapMarkerDto>> readMarkers, Action<string> requestRefresh, Func<float> clock)
    { this.readMarkers = readMarkers; this.requestRefresh = requestRefresh; this.clock = clock; }

    internal void Reset()
    { playerLocationCache.Clear(); playerLocationCachePlayersVersion = -1; cachedWorld = null; }

    internal bool CanLocate(ModPlayerSummaryDto player, int playerVersion)
    {
        return TryResolveCachedPlayerColonyTarget(player, playerVersion, out _, out _);
    }

    internal void Locate(ModPlayerSummaryDto player, int playerVersion)
    {
        if (!TryResolveCachedPlayerColonyTarget(player, playerVersion, out WorldObject? worldObject, out PlanetTile tile))
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Multiplayer.PlayerLocationUnavailable"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            playerLocationCache.Remove(PlayerLocationCacheKey(player));
            requestRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonLocatePlayer"));
            return;
        }

        if (worldObject is not null && worldObject.Spawned)
        {
            CameraJumper.TryJump(new GlobalTargetInfo(worldObject), CameraJumper.MovementMode.Pan);
            return;
        }

        CameraJumper.TryJump(tile, CameraJumper.MovementMode.Pan);
    }

    private bool TryResolveCachedPlayerColonyTarget(
        ModPlayerSummaryDto player,
        int playerVersion,
        out WorldObject? worldObject,
        out PlanetTile tile)
    {
        if (!ReferenceEquals(cachedWorld, Find.World) || playerLocationCachePlayersVersion != playerVersion)
        {
            playerLocationCache.Clear();
            playerLocationCachePlayersVersion = playerVersion;
            cachedWorld = Find.World;
        }

        string key = PlayerLocationCacheKey(player);
        float now = clock();
        if (playerLocationCache.TryGetValue(key, out PlayerLocationCacheEntry cached)
            && cached.ExpiresAt > now
            && (cached.WorldObject is null || cached.WorldObject.Spawned))
        {
            worldObject = cached.WorldObject;
            tile = cached.Tile;
            return cached.Found;
        }

        bool found = TryResolvePlayerColonyTarget(player, out worldObject, out tile);
        playerLocationCache[key] = new PlayerLocationCacheEntry
        {
            ExpiresAt = now + PlayerLocationCacheSeconds,
            Found = found,
            WorldObject = worldObject,
            Tile = tile
        };
        return found;
    }

    private bool TryResolvePlayerColonyTarget(
        ModPlayerSummaryDto player,
        out WorldObject? worldObject,
        out PlanetTile tile)
    {
        worldObject = null;
        tile = PlanetTile.Invalid;

        if (Find.WorldGrid is null)
        {
            return false;
        }

        IRemoteWorldObjectView? bestView = null;
        List<WorldObject>? worldObjects = Find.WorldObjects?.AllWorldObjects;
        if (worldObjects is not null)
        {
            for (int index = 0; index < worldObjects.Count; index++)
            {
                if (worldObjects[index] is not IRemoteWorldObjectView view
                    || !IsPlayerColonyViewForPlayer(view, player)
                    || !ClientWorldTileGeometry.Contains(Find.World, view.Tile))
                {
                    continue;
                }

                if (bestView is null || IsBetterPlayerColonyView(view, bestView))
                {
                    bestView = view;
                }
            }
        }

        worldObject = bestView?.WorldObject;
        if (worldObject is not null)
        {
            tile = worldObject.Tile;
            return true;
        }

        ModWorldMapMarkerDto? marker = null;
        List<ModWorldMapMarkerDto> markers = readMarkers();
        for (int index = 0; index < markers.Count; index++)
        {
            ModWorldMapMarkerDto candidate = markers[index];
            if (!IsPlayerColonyMarkerForPlayer(candidate, player)
                || !ClientWorldTileGeometry.Contains(Find.World, new PlanetTile(candidate.Tile, Math.Max(0, candidate.TileLayerId))))
            {
                continue;
            }

            if (marker is null || IsBetterPlayerColonyMarker(candidate, marker))
            {
                marker = candidate;
            }
        }

        if (marker is null)
        {
            return false;
        }

        tile = new PlanetTile(marker.Tile, Math.Max(0, marker.TileLayerId));
        return true;
    }

    private static bool IsBetterPlayerColonyView(IRemoteWorldObjectView candidate, IRemoteWorldObjectView current)
    {
        bool candidateIsMapParent = candidate.WorldObject is RemoteColonyMapParent;
        bool currentIsMapParent = current.WorldObject is RemoteColonyMapParent;
        if (candidateIsMapParent != currentIsMapParent)
        {
            return candidateIsMapParent;
        }

        return string.Compare(candidate.Label, current.Label, StringComparison.Ordinal) < 0;
    }

    private static bool IsBetterPlayerColonyMarker(ModWorldMapMarkerDto candidate, ModWorldMapMarkerDto current)
    {
        return string.Compare(candidate.Label, current.Label, StringComparison.Ordinal) < 0;
    }

    private static bool IsPlayerColonyViewForPlayer(IRemoteWorldObjectView view, ModPlayerSummaryDto player)
    {
        return view.RuntimeKind == "TradeableColony"
            && string.Equals(view.OwnerUserId, player.UserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(player.ColonyId)
                || string.Equals(view.OwnerColonyId, player.ColonyId, StringComparison.Ordinal));
    }

    private static bool IsPlayerColonyMarkerForPlayer(ModWorldMapMarkerDto marker, ModPlayerSummaryDto player)
    {
        return marker.Kind == "TradeableColony"
            && string.Equals(marker.OwnerUserId, player.UserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(player.ColonyId)
                || string.Equals(marker.OwnerColonyId, player.ColonyId, StringComparison.Ordinal));
    }

    private static string PlayerLocationCacheKey(ModPlayerSummaryDto player)
    {
        return (player.UserId ?? string.Empty) + "\u001f" + (player.ColonyId ?? string.Empty);
    }

    private sealed class PlayerLocationCacheEntry
    {
        public float ExpiresAt { get; set; }

        public bool Found { get; set; }

        public WorldObject? WorldObject { get; set; }

        public PlanetTile Tile { get; set; }
    }

}
