using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientWorldPollingTests
{
    internal static void Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        finally { ClientSessionWork.Reset(); }
    }
    private static async Task RunAsync()
    {
        var f = new ClientWorldSynchronizationTests.Fixture();
        Task View(bool selected, float at) => f.Pump(f.Flow.RefreshViewAsync(selected, at));
        await View(false, 0); Check(f.Calls.Count == 0, "Hidden world view refreshed.");
        await View(true, 10); await View(true, 39); Check(f.Calls.Count == 1, "World view did not refresh once on entry.");
        await View(true, 40); await View(true, 40); Check(f.Calls.Count == 2, "World view lost its 30-second boundary.");
        await View(false, 41); await View(true, 42); Check(f.Calls.Count == 3, "Reentering world view did not refresh immediately.");
        Check(f.TextKeys.Where(k => k.Contains("Reason")).SequenceEqual(new[] { "ClashOfRim.WorldMap.ReasonEnteredWorldMap",
            "ClashOfRim.WorldMap.ReasonStayedWorldMap", "ClashOfRim.WorldMap.ReasonEnteredWorldMap" }), "World view used the wrong entry/stay reasons.");
        f.Fault = "http"; await View(true, 72); f.Fault = null; await View(true, 72); await View(true, 102);
        Check(f.Calls.Count == 5 && f.Calls.All(c => c.Objects.Length == 0), "World view retried every frame, lost its next retry or uploaded objects.");
        f.Change("snapshot"); await View(true, 103); Check(f.Calls.Count == 5, "Ordinary snapshot advancement reset view polling.");
        foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "reset" })
        {
            var owner = new ClientWorldSynchronizationTests.Fixture();
            await owner.Pump(owner.Flow.RefreshViewAsync(true, 0)); owner.Change(change);
            await owner.Pump(owner.Flow.RefreshViewAsync(true, 1));
            Check(owner.Calls.Count == 2, "Old world-view deadline throttled new ownership: " + change);
            var replacing = new ClientWorldSynchronizationTests.Fixture(); replacing.Once("text", () => replacing.Change(change));
            await replacing.Pump(replacing.Flow.RefreshViewAsync(true, 0));
            Check(replacing.Calls.Count == 0, "View reason translation refreshed a replacement source: " + change);
            await replacing.Pump(replacing.Flow.RefreshViewAsync(true, 1));
            Check(replacing.Calls.Count == 1, "New view source inherited rejected translation's deadline: " + change);
        }
        var unavailable = new ClientWorldSynchronizationTests.Fixture(); unavailable.Sites.Session.BeginUnavailable(unavailable.Sites.Ports.Auth, null);
        await unavailable.Pump(unavailable.Flow.RefreshViewAsync(true, 0));
        Check(unavailable.Calls.Count == 0 && unavailable.TextKeys.Count == 0, "Unavailable session refreshed world view.");
        await EngineEntrypoints();
    }
    private static bool selected, available;
    private static float now;
    private static readonly List<Task> observed = new();
    private static WorldObjectsHolder objects = null!;
    private static async Task EngineEntrypoints()
    {
        var oldState = Current.ProgramState; var harmony = new Harmony("ClashOfRim.Tests.WorldPolling");
        using var runtime = new ClientGameComponentTestRuntime(harmony.Id);
        void Getter(Type type, string name, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, name), prefix: new HarmonyMethod(typeof(ClientWorldPollingTests), prefix));
        Getter(typeof(WorldRendererUtility), nameof(WorldRendererUtility.WorldSelected), nameof(Selected));
        Getter(typeof(Find), nameof(Find.WorldObjects), nameof(Objects));
        Getter(typeof(Time), nameof(Time.realtimeSinceStartup), nameof(TimeNow));
        foreach (string method in new[] { "ViewAsync", "TickAsync" })
            harmony.Patch(AccessTools.Method(typeof(ClientWorldSynchronizationCallbacks), method), postfix: new HarmonyMethod(typeof(ClientWorldPollingTests), nameof(Observe)));
        try
        {
            var f = new ClientWorldSynchronizationTests.Fixture(); var game = New<Game>(); Current.Game = game; f.Sites.Ports.GameOwner = game;
            Current.ProgramState = ProgramState.Playing; objects = New<WorldObjectsHolder>(); selected = available = true; now = 0;
            var component = new ClashOfRimGameComponent(game);
            component.GameComponentOnGUI(); component.GameComponentTick();
            Check(observed.Count == 0 && runtime.Drains == 2, "Unbound world callbacks changed normal component dispatch.");
            var callbacks = new ClientWorldSynchronizationCallbacks(f.Flow); ClientWorldSynchronizationCallbacks.Attach(callbacks);
            async Task Gui(float at) { now = at; component.GameComponentOnGUI(); await Pump(f); }
            await Gui(0); await Gui(29); await Gui(30);
            Check(f.Calls.Count == 2 && f.Calls.All(c => c.Objects.Length == 0), "GUI entry failed marker polling.");
            runtime.Ticks = f.Ticks = 0; component.GameComponentTick(); await Pump(f);
            Check(f.Calls.Count == 4 && f.Calls.Count(c => c.Objects.Length != 0) == 1 && f.Trades == 1, "Tick entry bypassed shared object/caravan polling.");
            runtime.Ticks = f.Ticks = 249; component.GameComponentTick(); await Pump(f); Check(f.Calls.Count == 4, "Component ignored module tick cadence.");
            selected = false; await Gui(60); selected = true; available = false; await Gui(60);
            Current.ProgramState = ProgramState.Entry; available = true; await Gui(60);
            Check(f.Calls.Count == 4, "Map-hidden, missing world or entry scene refreshed markers.");
            Current.ProgramState = ProgramState.Playing; await Gui(61); Check(f.Calls.Count == 5, "Returning to a valid world view did not refresh.");
            Current.Game = New<Game>(); f.Sites.Ports.GameOwner = Current.Game; f.Sites.World = new();
            await Gui(100); runtime.Ticks = f.Ticks = 10000; component.GameComponentTick(); await Pump(f);
            Check(f.Calls.Count == 5, "Old GameComponent polled the replacement world.");
            component = new ClashOfRimGameComponent(Current.Game); await Gui(100); Check(f.Calls.Count == 6, "New Game inherited an old view deadline.");
            ClientWorldSynchronizationCallbacks.Attach(new(f.Flow));
            await f.Pump(callbacks.ViewAsync(Current.Game)); await f.Pump(callbacks.TickAsync(Current.Game, 10000)); await Pump(f);
            Check(f.Calls.Count == 6, "Detached world adapter polled current state.");
            await Gui(130); Check(f.Calls.Count == 7, "Replacement adapter bypassed existing world module.");
        }
        finally { Current.ProgramState = oldState; observed.Clear(); harmony.UnpatchAll(harmony.Id); }
    }
    private static async Task Pump(ClientWorldSynchronizationTests.Fixture f) { var work = Task.WhenAll(observed.ToArray()); observed.Clear(); await f.Pump(work); }
    private static void Observe(Task __result) => observed.Add(__result);
    private static bool Selected(ref bool __result) { __result = selected; return false; }
    private static bool Objects(ref WorldObjectsHolder? __result) { __result = available ? objects : null; return false; }
    private static bool TimeNow(ref float __result) { __result = now; return false; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
