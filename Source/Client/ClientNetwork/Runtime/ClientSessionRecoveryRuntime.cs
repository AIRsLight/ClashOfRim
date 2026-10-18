using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionRecoveryRuntime : IClientSessionRecoveryRuntime
{
    public IClientSessionRecoverySession Bind() => new Bound(Current.Game, Find.World);
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.EnqueueSession(action, discard);
    public void ReturnToMenu() => GenScene.GoToMainMenu();
    private sealed record Bound(Game? Game, World? World) : IClientSessionRecoverySession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public bool IsPlaying => Current.ProgramState == ProgramState.Playing;
        public bool HasGame => Game is not null;
    }
}
