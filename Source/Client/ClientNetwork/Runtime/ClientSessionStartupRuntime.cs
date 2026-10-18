using System;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionStartupRuntime : IClientSessionStartupRuntime
{
    private readonly ClientSessionConnection connection;
    internal ClientSessionStartupRuntime(ClientSessionConnection connection) => this.connection = connection;
    public IClientSessionStartupSession Bind() => new Bound(Current.Game, Find.World);
    public bool IsPresenceRunning => connection.IsRunning;
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.EnqueueSession(action, discard);
    private sealed record Bound(Game? Game, World? World) : IClientSessionStartupSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool IsPlaying => Current.ProgramState == ProgramState.Playing;
        public bool HasMap => Find.CurrentMap is not null;
        public bool HasRaidBattle => ClashOfRimGameComponent.HasActiveRaidBattleSession;
        public bool IsObserving => ClashOfRimGameComponent.HasActiveObservationSession;
    }
}
