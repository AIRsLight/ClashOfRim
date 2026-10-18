using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientWorldSynchronizationRuntime : IClientWorldSynchronizationRuntime
{
    public IClientWorldSynchronizationSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldSynchronizationSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool HasWorldObjects => World?.worldObjects is not null;
        public bool HasRemoteSession => ClashOfRimGameComponent.HasActiveRaidBattleSession || ClashOfRimGameComponent.HasActiveObservationSession;
        public int Tick => Find.TickManager?.TicksGame ?? 0;
        public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects()
        {
            var result = new List<ModRuntimeWorldObjectMarkerDto>();
            foreach (var value in World?.worldObjects?.AllWorldObjects ?? new List<WorldObject>())
            {
                if (!Eligible(value) || !Valid(value.Tile)) continue;
                int layer = ClientWorldRuntimeValues.ReadPlanetTileLayerId(value.Tile);
                result.Add(new()
                {
                    WorldObjectId = ClientWorldColonySitesRuntime.ReadWorldObjectId(value, value.Tile.tileId, layer)!, DefName = value.def.defName,
                    Kind = Kind(value), Tile = value.Tile.tileId, TileLayerId = layer, Label = value.Label, PathTiles = Path(value, layer)
                });
            }
            return result;
        }
        public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans()
        {
            var result = new List<ModRuntimeWorldObjectMarkerDto>();
            foreach (var value in World?.worldObjects?.AllWorldObjects ?? new List<WorldObject>())
                if (value is Caravan caravan && !caravan.Destroyed && caravan.Spawned && caravan.IsPlayerControlled && Valid(caravan.Tile))
                    result.Add(new() { WorldObjectId = caravan.GetUniqueLoadID(), Tile = caravan.Tile.tileId, TileLayerId = ClientWorldRuntimeValues.ReadPlanetTileLayerId(caravan.Tile) });
            return result;
        }
        private bool Valid(PlanetTile tile) => ClientWorldTileGeometry.Contains(World, tile);
        private List<int>? Path(WorldObject value, int layer)
        {
            if (value is not Caravan caravan || caravan.pather?.Moving != true || caravan.pather.curPath is not { Found: true } path || path.NodesLeftCount < 2) return null;
            var result = new List<int>(path.NodesLeftCount);
            for (int i = 0; i < path.NodesLeftCount; i++)
            {
                var tile = path.Peek(i);
                // The wire path has no per-node layer. Do not flatten a layer transition.
                if (!Valid(tile) || ClientWorldRuntimeValues.ReadPlanetTileLayerId(tile) != layer) return null;
                result.Add(tile.tileId);
            }
            return result;
        }
    }
    private static bool Eligible(WorldObject value) => value is not null && !value.Destroyed && value.Spawned
        && value is not (RemoteRuntimeWorldObject or RemoteColonyMapParent or RemoteSessionMapParent or Settlement)
        && value.def is not null && value.Faction == Faction.OfPlayer
        && (value is Caravan || Token(value, "Caravan") || Token(value, "Shuttle") || Token(value, "TransportPod") || Token(value, "Traveling"));
    private static string Kind(WorldObject value) => value is Caravan || Token(value, "Caravan") ? "Caravan"
        : Token(value, "Shuttle") ? "Shuttle" : Token(value, "TransportPod") ? "TransportPod" : "WorldObject";
    private static bool Token(WorldObject value, string token) => value.GetType().Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
        || (value.def?.defName?.IndexOf(token, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
}
