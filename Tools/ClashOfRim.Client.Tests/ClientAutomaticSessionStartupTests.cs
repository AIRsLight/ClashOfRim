using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using HarmonyLib;
using Verse;

internal static class ClientAutomaticSessionStartupTests
{
    internal static void Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        finally { ClientSessionWork.Reset(); }
    }
    private static async Task RunAsync()
    {
        foreach (string gate in new[] { "map", "playing", "pending", "busy", "raid", "observation", "configured" }) await Deferred(gate);
        await FailedAttempt(); await ReentrantAttempt();
        foreach (string change in new[] { "user", "auth", "server", "colony", "game", "world", "world-id", "login", "work", "reset" }) await ChangedDuringCheck(change);
        await EngineEntrypoints();
    }
    private static async Task Deferred(string gate)
    {
        var f = ClientSessionStartupTests.AutomaticFixture(); bool current = true;
        var automatic = f.Module.CreateAutomatic(() => current); ClientSynchronizationCoordinator.Lease? lease = null;
        switch (gate)
        {
            case "map": f.HasMap = false; break;
            case "playing": f.Playing = false; break;
            case "pending": f.Mutations.Begin(LocalMutationKind.SnapshotRestore, "pending"); break;
            case "busy": Check(f.Sync.TryBeginManual(out lease), "Fixture admission failed."); break;
            case "raid": f.RaidBattle = true; break;
            case "observation": f.Observing = true; break;
            case "configured": f.Ports.Colony = ""; break;
        }
        await f.Ports.PumpUntil(automatic.PollAsync());
        Check(f.Calls.IsEmpty && f.Statuses.IsEmpty, "Automatic polling bypassed quiet admission: " + gate);
        lease?.Dispose(); f.Mutations.Reset(); f.HasMap = f.Playing = true; f.RaidBattle = f.Observing = false; f.Ports.Colony = "colony";
        current = false; await f.Ports.PumpUntil(automatic.PollAsync()); Check(f.Calls.IsEmpty, "Detached source started map login.");
        current = true; Task work = automatic.PollAsync(); await automatic.PollAsync(); await f.Ports.PumpUntil(work);
        Check(f.Calls.SequenceEqual(new[] { "login", "upload" }) && f.Session.SessionId == "accepted-session"
            && !f.Sync.IsBusy && f.Ports.Captures == 1, "Deferred automatic opportunity was consumed or duplicated: " + gate);
        f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "" });
        await f.Ports.PumpUntil(automatic.PollAsync());
        Check(f.Calls.Count == 2, "An already used game automatically retried after session removal.");
    }
    private static async Task FailedAttempt()
    {
        var f = ClientSessionStartupTests.AutomaticFixture(); f.FaultPhase = "login"; f.Fault = "network";
        var automatic = f.Module.CreateAutomatic(() => true);
        await f.Ports.PumpUntil(automatic.PollAsync()); f.Fault = null;
        await f.Ports.PumpUntil(automatic.PollAsync());
        Check(f.Calls.SequenceEqual(new[] { "login" }) && !f.Sync.IsBusy, "Failed automatic login became a per-frame retry loop.");
        await f.Ports.PumpUntil(f.Start());
        Check(f.Calls.SequenceEqual(new[] { "login", "login", "upload" }) && f.Session.SessionId == "accepted-session", "Automatic failure blocked explicit retry.");
    }
    private static async Task ReentrantAttempt()
    {
        var f = ClientSessionStartupTests.AutomaticFixture();
        var automatic = f.Module.CreateAutomatic(() => true); Task? reentered = null;
        f.Hook = step => { if (step == "login") reentered = automatic.PollAsync(); };
        await f.Ports.PumpUntil(automatic.PollAsync()); await reentered!;
        Check(f.Calls.SequenceEqual(new[] { "login", "upload" }), "Reentrant network callback duplicated automatic login.");
    }
    private static async Task ChangedDuringCheck(string change)
    {
        var f = ClientSessionStartupTests.AutomaticFixture(); int checks = 0;
        var automatic = f.Module.CreateAutomatic(() => { if (++checks == 2) f.Change(change); return true; });
        await f.Ports.PumpUntil(automatic.PollAsync());
        Check(checks == 2 && f.Calls.IsEmpty, "Automatic check launched a replacement source: " + change);
        f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "" });
        await f.Ports.PumpUntil(automatic.PollAsync());
        Check(f.Calls.SequenceEqual(new[] { "login", "upload" }), "Rejected source check consumed the automatic opportunity: " + change);
    }
    private static readonly List<Task> observed = new();
    private static async Task EngineEntrypoints()
    {
        var prior = ClientAutomaticSessionStartup.Current; var priorGame = Current.Game;
        var harmony = new Harmony("ClashOfRim.Tests.AutomaticSessionStartup");
        using var runtime = new ClientGameComponentTestRuntime(harmony.Id);
        harmony.Patch(AccessTools.Method(typeof(ClientAutomaticSessionStartup), nameof(ClientAutomaticSessionStartup.PollAsync)),
            postfix: new HarmonyMethod(typeof(ClientAutomaticSessionStartupTests), nameof(Observe)));
        try
        {
            var game = New<Game>(); Current.Game = game;
            var component = new ClashOfRimGameComponent(game); ClientAutomaticSessionStartup.Attach(null);
            component.GameComponentTick(); Check(observed.Count == 0 && runtime.Drains == 1, "Unbound component failed its normal queue callback.");
            var f = ClientSessionStartupTests.AutomaticFixture(); f.Ports.GameOwner = game; f.HasMap = false;
            var adapter = new ClientAutomaticSessionStartup(f.Module); ClientAutomaticSessionStartup.Attach(adapter);
            component.GameComponentTick(); await Pump(f); Check(f.Calls.IsEmpty, "Component consumed startup before map readiness.");
            f.HasMap = true; component.GameComponentTick(); component.GameComponentTick(); await Pump(f);
            Check(f.Calls.SequenceEqual(new[] { "login", "upload" }) && f.Ports.Captures == 1, "Tick entry bypassed original startup or ran twice.");
            f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "" });
            new ClashOfRimGameComponent(game).GameComponentTick(); await Pump(f);
            Check(f.Calls.Count == 2, "Another component reused the same Game's consumed automatic opportunity.");
            var nextGame = New<Game>(); Current.Game = nextGame; f.Ports.GameOwner = nextGame;
            f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "" });
            component.GameComponentTick(); await Pump(f);
            Check(f.Calls.Count == 2, "Old GameComponent started the replacement game's login.");
            var next = new ClashOfRimGameComponent(nextGame); next.GameComponentTick(); await Pump(f);
            Check(f.Calls.SequenceEqual(new[] { "login", "upload", "login", "upload" }), "New Game inherited the old game's used automatic opportunity.");
            var replacement = new ClientAutomaticSessionStartup(f.Module); ClientAutomaticSessionStartup.Attach(replacement);
            f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "" });
            await f.Ports.PumpUntil(adapter.PollAsync(nextGame)); await Pump(f);
            Check(f.Calls.Count == 4, "Detached automatic adapter started work.");
            next.GameComponentTick(); await Pump(f);
            Check(f.Calls.Count == 6, "Replacement adapter failed to start its own current game.");
        }
        finally { Current.Game = priorGame; ClientAutomaticSessionStartup.Attach(prior); observed.Clear(); harmony.UnpatchAll(harmony.Id); }
    }
    private static async Task Pump(ClientSessionStartupTests.Fixture f) { var work = Task.WhenAll(observed.ToArray()); observed.Clear(); await f.Ports.PumpUntil(work); }
    private static void Observe(Task __result) => observed.Add(__result);
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
