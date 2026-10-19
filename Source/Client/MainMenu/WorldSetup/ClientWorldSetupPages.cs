using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

internal sealed class ClientWorldSetupPages
{
    internal static ClientWorldSetupPages? Current { get; private set; }
    internal static void Attach(ClientWorldSetupPages? value) => Current = value;
    private readonly ClientWorldSetupLifecycle lifecycle;
    private readonly ConditionalWeakTable<Window, ClientWorldSetupLifecycle.Visit> visits = new();
    internal ClientWorldSetupPages(ClientWorldSetupLifecycle lifecycle) => this.lifecycle = lifecycle;
    internal Task OpenedAsync(Window window)
    {
        if (!visits.TryGetValue(window, out var visit))
        {
            visit = lifecycle.Open(new Page(this, window));
            visits.Add(window, visit);
        }
        return visit.StartAsync(Time.realtimeSinceStartup);
    }
    internal Task UpdatedAsync(Window window)
        => visits.TryGetValue(window, out var visit) ? visit.UpdateAsync(Time.realtimeSinceStartup) : Task.CompletedTask;
    internal void Closed(Window window)
    {
        if (!visits.TryGetValue(window, out var visit)) return;
        visits.Remove(window); visit.Close();
    }
    internal Task InitializedAsync(Game game) => lifecycle.InitializedAsync(game);
    private sealed record Page(ClientWorldSetupPages Owner, Window Window) : IClientWorldSetupPage
    { public bool IsOpen => ReferenceEquals(Current, Owner) && Find.WindowStack.IsOpen(Window); }
}
