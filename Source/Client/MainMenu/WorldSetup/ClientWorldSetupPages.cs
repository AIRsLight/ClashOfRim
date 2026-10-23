using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

internal sealed class ClientWorldSetupPages
{
    internal static ClientWorldSetupPages? Current { get; private set; }
    internal static void Attach(ClientWorldSetupPages? value)
    { Current?.SiteFilter?.Close(); Current = value; }
    private readonly ClientWorldSetupLifecycle lifecycle;
    private readonly ClientWorldSetupState setup;
    internal StartingSiteColonyFilter? SiteFilter { get; private set; }
    private readonly ConditionalWeakTable<Window, ClientWorldSetupLifecycle.Visit> visits = new();
    internal ClientWorldSetupPages(ClientWorldSetupLifecycle lifecycle, ClientWorldSetupState setup)
    { this.lifecycle = lifecycle; this.setup = setup; }
    internal Task OpenedAsync(Window window)
    {
        if (!visits.TryGetValue(window, out var visit))
        {
            visit = lifecycle.Open(new Page(this, window));
            visits.Add(window, visit);
            if (window is Page_SelectStartingSite && setup.ColonyPrepared && Find.World is { } world)
            {
                SiteFilter?.Close();
                SiteFilter = new(window, () => visit.IsCurrent, world);
            }
        }
        return visit.StartAsync(Time.realtimeSinceStartup);
    }
    internal Task UpdatedAsync(Window window)
        => visits.TryGetValue(window, out var visit) ? visit.UpdateAsync(Time.realtimeSinceStartup) : Task.CompletedTask;
    internal void Closed(Window window)
    {
        if (ReferenceEquals(SiteFilter?.Page, window)) { SiteFilter.Close(); SiteFilter = null; }
        if (!visits.TryGetValue(window, out var visit)) return;
        visits.Remove(window); visit.Close();
    }
    internal Task InitializedAsync(Game game) => lifecycle.InitializedAsync(game);
    internal void DrawSiteFilter(Window window)
    { if (ReferenceEquals(SiteFilter?.Page, window)) SiteFilter.Draw(); }
    private sealed record Page(ClientWorldSetupPages Owner, Window Window) : IClientWorldSetupPage
    { public bool IsOpen => ReferenceEquals(Current, Owner) && Find.WindowStack.IsOpen(Window); }
}
