using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientWorldMarkersRuntime : IClientWorldMarkersRuntime
{
    public IClientWorldMarkersSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldMarkersSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool HasWorldObjects => World?.worldObjects is not null;
        public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
        public ClientWorldMarkersApplied Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string user)
        {
            var result = RemoteRuntimeWorldObjectRegistry.Apply(markers, user);
            return new(result.Created > 0 || result.Removed > 0 || result.Failed > 0, result.ToString());
        }
    }
}
