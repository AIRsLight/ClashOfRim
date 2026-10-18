using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class PlayerDirectoryModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Protocol();
                foreach (bool game in new[] { true, false }) await Success(game);
            }
            await Admission(); await Replacement(); await Ownership(); await Failures(); await Selection(); await Actions(); await Cancellation();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task Protocol()
    {
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户-é", "colony", "snapshot", null, null, "auth");
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(ProtocolMessageKind.ListPlayers).Route.TrimStart('/'), "Player list route changed.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("userId").GetString() == context.UserId && body.RootElement.GetProperty("colonyId").GetString() == "colony"
                && body.RootElement.GetProperty("currentSnapshotId").GetString() == "snapshot" && body.RootElement.EnumerateObject().Count() == 3,
                "Player list wire fields lost captured identity or changed the existing contract.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"players\":[{\"userId\":\"bob\",\"colonyId\":\"c\",\"latestSnapshotWealth\":123}]}", Encoding.UTF8, "application/json") };
        }));
        var protocol = new ClientDiplomacyProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()), () => "unconfigured");
        Check((await protocol.ListAsync(context, default)).Response!.Players.Single().LatestSnapshotWealth == 123, "Directory protocol lost player fields.");
        Check((await new ClashOfRimModNetworkClient(http, context).ListPlayersAsync()).Success && calls == 2, "Public list facade did not forward to the shared protocol.");
        Check(!(await protocol.ListAsync(new("", "", "", null, null, null, null), default)).Success && calls == 2, "Unconfigured list reached HTTP.");
    }

    private static async Task Success(bool hasGame)
    {
        var f = new Fixture(); f.Ports.HasGame = hasGame;
        f.Reply.Players.Insert(1, new() { UserId = "registered", ColonyId = "" });
        f.Reply.Players.Add(new() { UserId = "charlie", ColonyId = "c", RelationKind = "99" });
        int version = f.Module.Version;
        var work = f.Module.RefreshAsync(true); var action = await f.Ports.Next();
        Check(f.Sync.IsBusy && f.Module.Busy && f.Module.Version == version && f.Module.CopyPlayers().Count == 0, "List escaped owned game publication.");
        f.Reply.Players[2].DisplayName = "changed after response";
        action(); await work;
        var copy = f.Module.CopyPlayers();
        Check(copy.Count == 4 && copy[2].DisplayName == "display-文化" && copy[2].LatestSnapshotWealth == 17 && copy[2].Online
            && copy[2].CurrentSnapshotId == "target-base" && f.Module.Version == version + 1 && !f.Sync.IsBusy && !f.Module.Busy,
            "Directory lost immutable fields, version or exact admission release.");
        copy[2].UserId = "changed by caller"; copy.Clear();
        Check(f.Module.CopyPlayers()[2].UserId == "bob" && f.Module.Selected == new ClientPlayerSelection("bob", "target", "target-base", "display-文化"), "Caller mutated the cache or default target skipped a valid colony.");
        Check(f.Trace.Count(t => t.StartsWith("ensure:")) == (hasGame ? 3 : 0) && f.Trace.Count(t => t.StartsWith("relation:")) == (hasGame ? 1 : 0)
            && f.Persisted == 1 && f.Ports.Captures == 0 && f.Ports.Published == 0, "Player projections used invalid/own relations or refreshed a local snapshot.");
        Check(f.Module.Message.StartsWith("ClashOfRim.Players.StatusRefreshed"), "Successful refresh has no status.");
        int finalVersion = f.Module.Version; f.Ports.Snapshot = "advanced";
        Check(f.Module.CopyPlayers().Count == 4 && f.Module.Version == finalVersion, "Ordinary snapshot advancement cleared the directory.");
        f.Ports.GameOwner = new();
        Check(f.Module.CopyPlayers().Count == 0 && f.Module.Version > finalVersion && f.Module.Message == "ClashOfRim.Status.PlayerListNotPulled", "Replacing Game retained its player state.");
    }

    private static async Task Admission()
    {
        foreach (bool manual in new[] { true, false })
        foreach (string condition in new[] { "server", "snapshot", "pending", "busy" })
        {
            var f = new Fixture(); ClientSynchronizationCoordinator.Lease? occupied = null;
            switch (condition)
            {
                case "server": f.Ports.Server = ""; break;
                case "snapshot": f.Ports.Snapshot = ""; break;
                case "pending": f.Mutations.Begin(LocalMutationKind.Bank, "pending"); break;
                case "busy": Check(f.Sync.TryBeginManual(out occupied), "Cannot seed occupied admission."); break;
            }
            var work = f.Module.RefreshAsync(manual); await f.Ports.PumpUntil(work);
            bool allowed = !manual && condition != "server";
            Check(f.Calls == (allowed ? 1 : 0) && f.Module.CopyPlayers().Count == (allowed ? 2 : 0) && !f.Module.Busy,
                "Manual/background list admission changed: " + manual + "/" + condition);
            if (occupied is not null) { Check(occupied.IsCurrent, "Background refresh released an unrelated operation."); occupied.Dispose(); }
        }
    }

    private static async Task Replacement()
    {
        foreach (bool queued in new[] { false, true })
        foreach (bool oldManual in new[] { false, true })
        {
            var f = new Fixture(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Send = async (call, token) => { if (call == 1 && !queued) await gate.Task.WaitAsync(token); return Reply(call == 1 ? "old" : "new"); };
            var old = f.Module.RefreshAsync(oldManual); Action? oldAction = null;
            if (queued) oldAction = await f.Ports.Next(); else await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var latest = f.Module.RefreshAsync(false); var latestAction = await f.Ports.Next();
            if (queued) oldAction!(); else gate.SetResult();
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.Busy && f.Module.CopyPlayers().Count == 0 && f.Trace.Count == 0, "Old completion published data or cleared the latest request.");
            latestAction(); await latest;
            Check(f.Module.CopyPlayers().Last().UserId == "new" && f.Module.Selected.UserId == "new" && !f.Module.Busy && !f.Sync.IsBusy
                && f.Trace.All(t => !t.Contains(":old")), "Superseded list changed the new projection or selected target.");
        }
        var reentry = new Fixture(); ClientSynchronizationCoordinator.Lease? replacement = null;
        reentry.OnPersist = () => { reentry.Module.Reset(); Check(reentry.Sync.TryBeginManual(out replacement), "Reset did not release the old directory lease."); };
        await reentry.Ports.PumpUntil(reentry.Module.RefreshAsync(true));
        Check(replacement!.IsCurrent, "Old finalizer released replacement admission."); replacement.Dispose();
    }

    private static async Task Ownership()
    {
        foreach (string phase in new[] { "send", "queued", "ensure", "relation", "write", "persist", "summary" })
        foreach (string change in new[] { "user", "auth", "server", "colony", "game", "login", "generation", "lease", "reset" })
        {
            var f = new Fixture(); Action changed = () => f.Change(change);
            switch (phase)
            {
                case "send": f.OnSend = changed; break;
                case "queued": f.Ports.BeforeGameAction = changed; break;
                case "ensure": f.OnEnsure = changed; break;
                case "relation": f.OnRelation = changed; break;
                case "write": f.OnWrite = changed; break;
                case "persist": f.OnPersist = changed; break;
                case "summary": f.OnSummary = changed; break;
            }
            var work = f.Module.RefreshAsync(true); await f.Ports.PumpUntil(work);
            int count = phase switch { "ensure" => 1, "relation" => 2, "write" => 3, "persist" => 4, "summary" => 5, _ => 0 };
            Check(f.Trace.Count == count && !f.Module.Message.StartsWith("ClashOfRim.Players.StatusRefreshed") && !f.Module.Busy && !f.Sync.IsBusy,
                "Directory crossed an ownership boundary: " + phase + "/" + change + ": " + string.Join(',', f.Trace));
        }
        var advanced = new Fixture(); advanced.OnSend = () => advanced.Ports.Snapshot = "advanced";
        await advanced.Ports.PumpUntil(advanced.Module.RefreshAsync(true));
        Check(advanced.Module.CopyPlayers().Count == 2 && advanced.Context!.CurrentSnapshotId == "base", "Read request borrowed a later base or refused valid snapshot advancement.");
    }

    private static async Task Failures()
    {
        foreach (string failure in new[] { "transport", "exception", "rejected", "missing-result", "missing-players", "null-player" })
        {
            var f = new Fixture(); await f.Ports.PumpUntil(f.Module.RefreshAsync(false));
            int version = f.Module.Version, effects = f.Trace.Count;
            switch (failure)
            {
                case "transport": f.Send = (_, _) => Task.FromResult<ModListPlayersResponseDto>(null!); break;
                case "exception": f.Send = (_, _) => throw new HttpRequestException("offline"); break;
                case "rejected": f.Reply.Result!.Accepted = false; break;
                case "missing-result": f.Reply.Result = null; break;
                case "missing-players": f.Reply.Players = null!; break;
                case "null-player": f.Reply.Players.Add(null!); break;
            }
            await f.Ports.PumpUntil(f.Module.RefreshAsync(true));
            Check(f.Module.Version == version && f.Module.CopyPlayers().Count == 2 && f.Trace.Count == effects && !f.Sync.IsBusy && !f.Module.Busy
                && !f.Module.Message.StartsWith("ClashOfRim.Players.StatusRefreshed"), "Failed/incomplete list destroyed the previous state: " + failure);
        }
        var broken = new Fixture(); broken.OnEnsure = () => throw new InvalidOperationException("proxy failed");
        await broken.Ports.PumpUntil(broken.Module.RefreshAsync(true), _ => { });
        Check(broken.Trace.SequenceEqual(new[] { "ensure:bob" }) && broken.Diagnostics == 1 && !broken.Sync.IsBusy, "Proxy exception continued publication or leaked admission.");
        broken.OnEnsure = null; await broken.Ports.PumpUntil(broken.Module.RefreshAsync(true));
        Check(broken.Module.Message.StartsWith("ClashOfRim.Players.StatusRefreshed"), "Failed projection could not refresh again.");
    }

    private static async Task Selection()
    {
        var f = new Fixture(); f.Reply.Players.Add(new() { UserId = "bob", ColonyId = "second", CurrentSnapshotId = "second-base" });
        await f.Ports.PumpUntil(f.Module.RefreshAsync(false));
        f.Module.SelectNext(); Check(f.Module.Selected.ColonyId == "second", "Target rotation matched only the account ID.");
        f.Module.SelectNext(); Check(f.Module.Selected.ColonyId == "target", "Target rotation failed to wrap.");
        Check(!f.Module.Select(new("alice", "colony")) && !f.Module.Select(new("", "colony")) && !f.Module.Select(new("bob", "")), "Invalid/self selection was persisted.");
        Check(f.Module.Select(new("alice", "another-colony")), "Explicit selection rejected another colony of the same account.");
        var work = f.Module.RefreshAsync(false); var queued = await f.Ports.Next();
        Check(f.Module.Select(new("chosen", "chosen-colony")), "Could not select a target while a list awaited publication.");
        queued(); await work; Check(f.Module.Selected.UserId == "chosen", "Automatic target selection overwrote an explicit choice.");
        f.Reply.Players.Clear(); await f.Ports.PumpUntil(f.Module.RefreshAsync(false));
        f.Module.SelectNext(); Check(f.Module.Message == "ClashOfRim.Players.StatusNoSelectableTargets" && f.Module.Selected.UserId == "chosen", "Empty target list changed selection.");
        int persisted = f.Persisted; f.Module.ClearSelection(); f.Module.Reset();
        Check(f.Module.Selected == new ClientPlayerSelection("", "", "") && f.Persisted == persisted && f.Module.CopyPlayers().Count == 0, "Lifecycle reset persisted partial account settings.");
        foreach (bool duringWrite in new[] { true, false })
        {
            var stale = new Fixture(); if (duringWrite) stale.OnWrite = () => stale.Change("game"); else stale.OnPersist = () => stale.Change("game");
            Check(!stale.Module.Select(new("bob", "target")) && stale.Persisted == (duringWrite ? 0 : 1)
                && !stale.Module.Message.StartsWith("ClashOfRim.Players.StatusSelectedTarget"), "Old selection persisted or reported into another Game.");
        }
        var settings = new ClashOfRimSettings(); var store = new ClientPlayerSelectionStore(settings);
        store.Write(new("user", "colony", "snapshot", "display"));
        Check(store.Read() == new ClientPlayerSelection("user", "colony", "snapshot"), "Selection adapter changed the persisted format.");
        store.Write(new("user", "colony")); Check(settings.TargetSnapshotId == "", "Missing selection snapshot retained an old snapshot.");
    }

    private static async Task Actions()
    {
        var f = new Fixture(); var target = Reply().Players.Last();
        f.OnPersist = () => { target.UserId = "edited"; target.ColonyId = "edited"; target.DisplayName = "edited"; };
        Check(f.Actions.Create("WarDeclaration", target, "message", out var work), "Explicit player action did not start.");
        await f.Ports.PumpUntil(work);
        Check(f.Command?.Target == new ClientDiplomacyTarget("bob", "target", "display-文化") && f.Command.Message == "message"
            && f.Module.Selected.SnapshotId == "target-base" && f.Creations == 1, "Selection persistence changed the frozen diplomacy target.");
        var current = new Fixture(); current.Selected = new("selected", "selected-colony");
        Check(current.Actions.Create("PeaceRequest", "message", "name", out work), "Selected player action did not start.");
        await current.Ports.PumpUntil(work);
        Check(current.Command?.Target == new ClientDiplomacyTarget("selected", "selected-colony", "name"), "Selected player action borrowed another target.");
        foreach (string change in new[] { "game", "login", "generation" })
        {
            var stale = new Fixture(); stale.OnPersist = () => stale.Change(change);
            Check(!stale.Actions.Create("WarDeclaration", Reply().Players.Last(), null, out work) && work.IsCompleted && stale.Creations == 0,
                "Selection started a diplomacy command after ownership changed: " + change);
        }
        Check(!current.Actions.Select(null) && !current.Actions.Create("WarDeclaration", (ModPlayerSummaryDto?)null, null, out _)
            && !current.Actions.Create("WarDeclaration", Reply().Players.First(), null, out _), "Legacy action accepted an invalid/self target.");
    }

    private static async Task Cancellation()
    {
        foreach (bool queued in new[] { true, false })
        {
            var f = new Fixture(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queued) f.Send = async (_, token) => { await gate.Task.WaitAsync(token); return f.Reply; };
            var work = f.Module.RefreshAsync(true); Action? old = null;
            if (queued) old = await f.Ports.Next(); else await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Change("generation"); Check(f.Sync.TryBeginManual(out var next), "Cannot start replacement operation.");
            await work.WaitAsync(TimeSpan.FromSeconds(5)); old?.Invoke();
            Check(f.Trace.Count == 0 && next!.IsCurrent && f.Module.CopyPlayers().Count == 0, "Canceled list crossed session exit or released new work.");
            next!.Dispose();
        }
    }

    private static ModListPlayersResponseDto Reply(string target = "bob") => new()
    {
        Result = new() { Accepted = true }, Players = new()
        {
            new() { UserId = "alice", ColonyId = "colony", RelationKind = "Hostile" },
            new() { UserId = target, ColonyId = "target", CurrentSnapshotId = "target-base", DisplayName = "display-文化", Online = true, RelationKind = "aLlY", LatestSnapshotWealth = 17 }
        }
    };
    private sealed class Fixture : IClientPlayerDirectoryTransport, IClientPlayerSelectionStore, IClientPlayerDirectoryPresentation, IClientPlayerDirectoryRuntime,
        IClientDiplomacyCreationTransport, IClientDiplomacyCreationPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientPlayerDirectory Module;
        internal readonly ClientDiplomacyActions Actions;
        internal ClientDiplomacyCommand? Command;
        internal int Creations;
        internal ModListPlayersResponseDto Reply = PlayerDirectoryModuleTests.Reply();
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Trace = new();
        internal int Calls, Persisted, Diagnostics;
        internal ClashOfRimClientNetworkContext? Context;
        internal ClientPlayerSelection Selected = new("", "");
        internal Action? OnSend, OnEnsure, OnRelation, OnWrite, OnPersist, OnSummary;
        internal Func<int, CancellationToken, Task<ModListPlayersResponseDto>>? Send;
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Module = new(Session, Ports, Sync, Mutations, this, this, this, this);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new(Ports, Ports));
            Actions = new(Module, new(new(Session, Sync, Mutations, transactions), transactions, Mutations, this, this));
        }
        public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token)
        { Creations++; Command = command; return Task.FromResult(ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>.Ok(new() { Result = new() { Accepted = true }, EventId = "created", RelationKind = "Neutral" })); }
        public string DefaultMessage(string kind) => "default:" + kind;
        public void Report(ClientDiplomacyCreationNotice notice) { }
        public void ApplyRelation(string userId, string relation, string kind) => ApplyRelation(userId, relation);
        public async Task<ClashOfRimClientNetworkResult<ModListPlayersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            int call = ++Calls; Context = context; OnSend?.Invoke(); Sent.TrySetResult();
            var response = Send is null ? Reply : await Send(call, token);
            return response is null ? ClashOfRimClientNetworkResult<ModListPlayersResponseDto>.Failed("offline", "network") : ClashOfRimClientNetworkResult<ModListPlayersResponseDto>.Ok(response);
        }
        public ClientPlayerSelection Read() => Selected;
        public void Write(ClientPlayerSelection selection) { Trace.Add("write:" + selection.UserId); Selected = selection; OnWrite?.Invoke(); }
        public void Persist() { Trace.Add("persist"); Persisted++; OnPersist?.Invoke(); }
        public string Text(string key, params (string Name, object? Value)[] arguments) => key + (arguments.Length == 0 ? "" : ":" + string.Join(',', arguments.Select(a => a.Value)));
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => "admission:" + admission;
        public string Summary(IReadOnlyCollection<ModPlayerSummaryDto> players) { Trace.Add("summary"); OnSummary?.Invoke(); return players.Count.ToString(CultureInfo.InvariantCulture); }
        public void Diagnose(Exception exception) => Diagnostics++;
        public void EnsureProxy(string userId) { Trace.Add("ensure:" + userId); OnEnsure?.Invoke(); }
        public void ApplyRelation(string userId, string relation) { Trace.Add("relation:" + userId + ":" + relation); OnRelation?.Invoke(); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "lease": Sync.Reset(); break;
                case "reset": Module.Reset(); break;
            }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
