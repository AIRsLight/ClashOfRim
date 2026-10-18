using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldCatalogRuntime : IClientWorldCatalogRuntime
{
    public IClientWorldCatalogSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldCatalogSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
    }
}
