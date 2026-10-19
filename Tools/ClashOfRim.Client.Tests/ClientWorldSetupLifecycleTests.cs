using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class ClientWorldSetupLifecycleTests
{
    private static readonly string[] Changes = { "user", "auth", "colony", "server", "game", "world", "login", "work", "unavailable" };
    internal static void Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        finally { ClientSessionWork.Reset(); }
    }
    private static async Task RunAsync()
    {
        await OpenAndInitialize(); await Deadlines(); await SourceGame(); await PendingSubmission();
        foreach (string change in Changes)
        {
            await Stale(change, beforeStart: true);
            await Stale(change, beforeStart: false);
            await Callback(change, projection: false);
            await Callback(change, projection: true);
        }
        await EngineEntrypoints();
    }
    private static async Task OpenAndInitialize()
    {
        var f = new Fixture(initial: true); var page = new Page(); var visit = f.Flow.Open(page);
        Task opened = visit.StartAsync(10); await visit.StartAsync(20);
        await f.Core.Ports.PumpUntil(Task.WhenAll(opened, f.Flow.InitializedAsync(f.Core.Ports.GameOwner)));
        Check(f.Core.Calls.SequenceEqual(new[] { "metadata", "world-upload", "requirements", "admin-submit" })
            && f.Core.Successes == 1 && !f.Core.Setup.Pending && !f.Core.Sync.IsBusy, "Page and initialization duplicated or bypassed initial provisioning.");
        Check(f.Reads == 1 && f.Reasons.SequenceEqual(new[] { "ClashOfRim.WorldMap.ReasonStartingSiteEntered" }), "Opening missed its immediate marker refresh.");
        f.Core.Ports.Snapshot = "next"; f.Publication.LocalWorldId = "published-world";
        await f.Pump(visit.UpdateAsync(40));
        Check(f.Reads == 2, "Legitimate snapshot/world ID publication invalidated the same setup page.");
    }
    private static async Task Deadlines()
    {
        var f = new Fixture(); var firstPage = new Page(); var secondPage = new Page();
        var first = f.Flow.Open(firstPage); var second = f.Flow.Open(secondPage);
        await f.Pump(first.UpdateAsync(100)); Check(f.Reads == 0, "Unstarted visit refreshed.");
        await f.Pump(first.StartAsync(10)); await f.Pump(second.StartAsync(25));
        await f.Pump(first.UpdateAsync(39)); Check(f.Reads == 2, "Visit refreshed before its deadline.");
        await f.Pump(first.UpdateAsync(40)); Check(f.Reads == 3, "Opening another page moved the first page's deadline.");
        await f.Pump(first.UpdateAsync(40)); await f.Pump(second.UpdateAsync(54)); Check(f.Reads == 3, "Repeated GUI callback or another page refreshed early.");
        first.Close(); first.Close(); await f.Pump(first.UpdateAsync(100)); await f.Pump(first.StartAsync(100));
        Check(f.Reads == 3, "Closed visit restarted.");
        await f.Pump(second.UpdateAsync(55)); Check(f.Reads == 4, "Closing an older visit stopped its replacement.");
        int projections = f.Publication.MarkerApplications.Count;
        secondPage.Open = false; await f.Pump(second.UpdateAsync(100));
        Check(f.Reads == 4 && f.Publication.MarkerApplications.Count == projections, "Removed page still refreshed/projected cached markers.");
    }
    private static async Task Stale(string change, bool beforeStart)
    {
        var f = new Fixture(initial: beforeStart); var visit = f.Flow.Open(new Page());
        if (!beforeStart) await f.Pump(visit.StartAsync(0));
        int reads = f.Reads, projections = f.Publication.MarkerApplications.Count;
        f.Core.Change(change);
        await f.Pump(visit.StartAsync(40)); await f.Pump(visit.UpdateAsync(40));
        Check(f.Reads == reads && f.Publication.MarkerApplications.Count == projections && f.Core.Calls.IsEmpty,
            "Old setup visit crossed " + change + (beforeStart ? " before opening." : " during polling."));
    }
    private static async Task Callback(string change, bool projection)
    {
        var f = new Fixture(); var visit = f.Flow.Open(new Page()); await f.Pump(visit.StartAsync(0));
        bool changed = false;
        void Replace() { if (changed) return; changed = true; f.Core.Change(change); }
        if (projection) f.Publication.ApplyingMarkers = _ => Replace();
        else f.Translating = _ => Replace();
        await f.Pump(visit.UpdateAsync(30));
        Check(changed && f.Reads == 1, "Setup refresh crossed " + change + " during " + (projection ? "projection." : "translation."));
    }
    private static async Task SourceGame()
    {
        var f = new Fixture(initial: true); object old = f.Core.Ports.GameOwner;
        f.Source.BlockCurrent(); await f.Pump(f.Flow.InitializedAsync(old));
        Check(f.Source.BlocksCurrent && f.Core.Calls.IsEmpty && f.Core.Setup.Pending, "Blocked old scene initialized setup.");
        f.Core.Change("game"); await f.Pump(f.Flow.InitializedAsync(old));
        Check(f.Core.Calls.IsEmpty, "Unrelated Game initialization consumed pending setup.");
        await f.Pump(f.Flow.InitializedAsync(f.Core.Ports.GameOwner));
        Check(!f.Source.BlocksCurrent && f.Core.Successes == 1 && !f.Core.Setup.Pending, "Current new Game failed original provisioning.");
        f.Core.Ports.GameOwner = old;
        Check(!f.Source.BlocksCurrent, "Successful new-game initialization retained the old source barrier.");
    }
    private static async Task PendingSubmission()
    {
        var f = new Fixture(initial: true); var delayed = new ClientWorldPublicationTestPorts.DelayedReply();
        f.Core.Before = step => step == "metadata" ? delayed.Wait() : Task.CompletedTask;
        var visit = f.Flow.Open(new Page()); var work = visit.StartAsync(0);
        await delayed.Complete(f.Core.Ports, work, visit.Close);
        Check(f.Core.Successes == 1 && !f.Core.Sync.IsBusy, "Closing the page abandoned already admitted initial provisioning.");
        await f.Pump(visit.UpdateAsync(30)); Check(f.Reads == 1, "Closed page continued polling after submission completed.");
    }
    private sealed class Page : IClientWorldSetupPage { internal bool Open = true; public bool IsOpen => Open; }
    private sealed class Fixture : IClientWorldCatalogRuntime, IClientWorldSynchronizationRuntime, IClientWorldMarkerTransport,
        IClientWorldSynchronizationPresentation, IClientWorldSourceGame
    {
        internal readonly ClientInitialWorldSetupTests.Fixture Core = new(playing: false);
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal readonly ClientWorldSourceGame Source;
        internal readonly ClientWorldSetupLifecycle Flow;
        internal readonly List<string> Reasons = new();
        internal int Reads;
        internal Action<string>? Translating;
        internal Fixture(bool initial = false)
        {
            if (!initial) Core.Setup.Reset();
            Publication = new(Core.Session, Core.Ports, world: () => Core.World);
            Source = new(this);
            var synchronization = new ClientWorldSynchronization(Core.Session, Publication, new(), Publication.Markers, this, this, this,
                () => throw new InvalidOperationException("Page refresh captured caravan trades."));
            Flow = new(Core.Session, this, Source, Core.Module, Publication.Appearance, synchronization, Publication.Markers, this);
        }
        internal Task Pump(Task work) => Core.Ports.PumpUntil(work);
        public object Current => Core.Ports.GameOwner;
        IClientWorldCatalogSession IClientWorldCatalogRuntime.Bind() => new Bound(this, Current, Core.World);
        IClientWorldSynchronizationSession IClientWorldSynchronizationRuntime.Bind() => new Bound(this, Current, Core.World);
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            if (key.Contains("ReasonStartingSite")) { Reasons.Add(key); Translating?.Invoke(key); }
            return key;
        }
        public void Status(string text) { }
        public void Diagnose(string text) { }
        public Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        { Interlocked.Increment(ref Reads); return Task.FromResult(ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>.Ok(new() { UserId = context.UserId, Markers = new() })); }
        public Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context,
            IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token) => throw new InvalidOperationException("Page refresh uploaded runtime objects.");
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldCatalogSession, IClientWorldSynchronizationSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Current) && ReferenceEquals(World, F.Core.World);
            public bool HasWorldObjects => true; public bool HasRemoteSession => false; public int Tick => 0;
            public void Enqueue(Action action, Action discard) => F.Core.Ports.Enqueue(action);
            public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects() => throw new NotSupportedException();
            public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans() => throw new NotSupportedException();
        }
    }
    private static readonly List<Window> windows = new();
    private static readonly List<Task> observed = new();
    private static WindowStack stack = null!;
    private static float now;
    private static int drains;
    private static async Task EngineEntrypoints()
    {
        var prior = ClientWorldSetupPages.Current;
        var harmony = new Harmony("ClashOfRim.Tests.WorldSetupLifecycle");
        void Prefix(System.Reflection.MethodBase method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientWorldSetupLifecycleTests), name));
        Prefix(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(GetStack));
        Prefix(AccessTools.PropertyGetter(typeof(WindowStack), nameof(WindowStack.Count)), nameof(Count));
        Prefix(AccessTools.PropertyGetter(typeof(WindowStack), "Item"), nameof(Item));
        Prefix(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.IsOpen), new[] { typeof(Window) }), nameof(IsOpen));
        Prefix(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), nameof(TimeNow));
        Prefix(AccessTools.Method(typeof(ClientWorldSetupDiagnostics), nameof(ClientWorldSetupDiagnostics.Log)), nameof(SkipLog));
        Prefix(AccessTools.Method(typeof(ClashOfRimMainMenuPatches), nameof(ClashOfRimMainMenuPatches.RunQueuedMainThreadActions)), nameof(Drain));
        foreach (string name in new[] { "OpenedAsync", "UpdatedAsync", "InitializedAsync" })
            harmony.Patch(AccessTools.Method(typeof(ClientWorldSetupPages), name), postfix: new HarmonyMethod(typeof(ClientWorldSetupLifecycleTests), nameof(Observe)));
        try
        {
            stack = New<WindowStack>(); drains = 0;
            var page = New<Page_SelectStartingSite>(); var second = New<Page_SelectStartingSite>();
            windows.Add(page); windows.Add(second); ClientWorldSetupPages.Attach(null);
            ClashOfRimStartingSitePostOpenPatch.Postfix(page); ClashOfRimStartingSiteExtraOnGuiPatch.Postfix(page);
            ClashOfRimStartingSitePostClosePatch.Postfix(page); ClashOfRimWorldSetupPatches.Postfix(New<Game>());
            Check(observed.Count == 0 && drains == 1, "Unbound page changed vanilla flow or stopped the main-thread queue.");
            var f = new Fixture(initial: true); var adapter = new ClientWorldSetupPages(f.Flow); ClientWorldSetupPages.Attach(adapter);
            var game = New<Game>(); f.Core.Ports.GameOwner = game;
            now = 10; ClashOfRimStartingSitePostOpenPatch.Postfix(page); ClashOfRimStartingSitePostOpenPatch.Postfix(page);
            ClashOfRimWorldSetupPatches.Postfix(game); await PumpObserved(f);
            Check(f.Reads == 1 && f.Core.Successes == 1, "Real page/InitNewGame patches bypassed or duplicated the shared modules.");
            now = 25; ClashOfRimStartingSitePostOpenPatch.Postfix(second); await PumpObserved(f);
            now = 40; ClashOfRimStartingSiteExtraOnGuiPatch.Postfix(page); await PumpObserved(f);
            Check(f.Reads == 3 && drains == 2, "Native page adapter shared its deadline or skipped queue draining.");
            ClashOfRimStartingSitePostClosePatch.Postfix(page);
            now = 55; ClashOfRimStartingSiteExtraOnGuiPatch.Postfix(page); ClashOfRimStartingSiteExtraOnGuiPatch.Postfix(second); await PumpObserved(f);
            Check(f.Reads == 4, "Close patch failed to retire one visit independently.");
            ClientWorldSetupPages.Attach(new(f.Flow));
            now = 90; int projections = f.Publication.MarkerApplications.Count;
            await f.Pump(adapter.UpdatedAsync(second)); await PumpObserved(f);
            Check(f.Reads == 4 && f.Publication.MarkerApplications.Count == projections, "Detached page adapter refreshed/projected markers.");
            now = 100; ClashOfRimStartingSitePostOpenPatch.Postfix(second); await PumpObserved(f);
            Check(f.Reads == 5, "Replacement adapter could not open its own visit.");
            windows.Remove(second); now = 130; ClashOfRimStartingSiteExtraOnGuiPatch.Postfix(second); await PumpObserved(f);
            Check(f.Reads == 5, "Page absent from native WindowStack continued polling.");
        }
        finally { ClientWorldSetupPages.Attach(prior); windows.Clear(); observed.Clear(); harmony.UnpatchAll(harmony.Id); }
    }
    private static async Task PumpObserved(Fixture f) { var work = Task.WhenAll(observed.ToArray()); observed.Clear(); await f.Pump(work); }
    private static void Observe(Task __result) => observed.Add(__result);
    private static bool GetStack(ref WindowStack __result) { __result = stack; return false; }
    private static bool Count(ref int __result) { __result = windows.Count; return false; }
    private static bool Item(int __0, ref Window __result) { __result = windows[__0]; return false; }
    private static bool IsOpen(Window __0, ref bool __result) { __result = windows.Contains(__0); return false; }
    private static bool TimeNow(ref float __result) { __result = now; return false; }
    private static bool SkipLog() => false;
    private static bool Drain() { drains++; return false; }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
