using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientWorldSynchronizationCallbacks
{
    internal static ClientWorldSynchronizationCallbacks? Current { get; private set; }
    internal static void Attach(ClientWorldSynchronizationCallbacks? value) => Current = value;
    private readonly ClientWorldSynchronization synchronization;
    internal ClientWorldSynchronizationCallbacks(ClientWorldSynchronization synchronization) => this.synchronization = synchronization;
    private bool Owns(Game game) => ReferenceEquals(Current, this) && ReferenceEquals(game, Verse.Current.Game);
    internal Task TickAsync(Game game, int ticks) => Owns(game) ? synchronization.Poll(ticks) : Task.CompletedTask;
    internal Task ViewAsync(Game game)
    {
        if (!Owns(game)) return Task.CompletedTask;
        bool selected = Verse.Current.ProgramState == ProgramState.Playing && WorldRendererUtility.WorldSelected && Find.WorldObjects is not null;
        return synchronization.RefreshViewAsync(selected, Time.realtimeSinceStartup);
    }
}
