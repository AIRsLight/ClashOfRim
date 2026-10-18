using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldBaselineRuntime : IClientWorldBaselineRuntime
{
    public IClientWorldBaselineSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldBaselineSession
    {
        public bool IsCurrent => Game is not null && World is not null
            && ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
    }
}
