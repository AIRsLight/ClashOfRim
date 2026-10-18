using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientOccupiedColonySitesRuntime : IClientOccupiedColonySitesRuntime
{
    public IClientOccupiedColonySitesSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientOccupiedColonySitesSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool CanReserve(int tile, int layer) => World?.grid?.PlanetLayers is { } layers
            && layers.TryGetValue(layer, out var value) && !value.Def.isSpace && tile >= 0 && tile < value.TilesCount;
        public bool IsNeighborOrSame(int tile, int other, int layer)
            => World!.grid.IsNeighborOrSame(new PlanetTile(tile, layer), new PlanetTile(other, layer));
        public void EnsureProxy(string user)
        {
            if (IsCurrent && World is not null) PlayerFactionProxyUtility.EnsureProxyForUser(user);
        }
    }
}
