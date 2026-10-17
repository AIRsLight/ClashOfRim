using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventConfirmationModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await WireContracts();
            await SuccessfulPublication();
            await Failures();
            await ReplacedOwners();
            await LateCompletions();
            RetryPolicy();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task WireContracts()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户", "colony", "base", null, null, "auth");
                int calls = 0, diagnostics = 0;
                using var http = new HttpClient(new Handler(async request =>
                {
                    int index = calls++;
                    bool batch = index == 2;
                    Require(request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(batch
                        ? ProtocolMessageKind.ConfirmEventApplications : ProtocolMessageKind.ConfirmEventApplication).Route.TrimStart('/'), "Confirmation route changed.");
                    var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                    Require(parts.Length == 2 && (await parts[1].ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1, 2, 3 }), "Snapshot payload changed.");
                    using var body = JsonDocument.Parse(await parts[0].ReadAsStringAsync());
                    var json = body.RootElement;
                    Require(json.GetProperty("authToken").GetString() == "auth" && json.GetProperty("userId").GetString() == (index == 1 ? "target" : "用户")
                        && json.GetProperty("colonyId").GetString() == (index == 1 ? "target-colony" : "colony"), "Prepared target or authenticated owner was lost.");
                    Require(json.GetProperty("baseSnapshotId").GetString() == "base" && json.GetProperty("idempotencyKey").GetString() == "key", "Snapshot/idempotency contract changed.");
                    Require(json.GetProperty("confirmedSnapshot").GetProperty("snapshotUploadKind").GetString() == (batch
                        ? ModSnapshotUploadKinds.BatchEventApplicationConfirmation : ModSnapshotUploadKinds.EventApplicationConfirmation), "Snapshot upload kind changed.");
                    if (batch) Require(json.GetProperty("applications").EnumerateArray().Select(x => x.GetProperty("eventId").GetString()).SequenceEqual(new[] { "é", "A" }), "Batch wire order changed.");
                    else Require(json.GetProperty("eventId").GetString() == "event" && json.GetProperty("sourceEventId").GetString() == "source"
                        && json.GetProperty("clientApplicationResult").GetString() == "arrived", "Application contract changed.");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }));
                var protocol = new ClientEventConfirmationProtocol(new ClientProtocolTransport(http, context, new Environment()), () => { diagnostics++; return "unconfigured"; });
                var single = new ClientEventConfirmationCommand("key", "event", "source", "用户", "colony", "base", Package(), new byte[] { 1, 2, 3 }, "arrived");
                Require((await protocol.ConfirmAsync(context, single, default)).Success, "Single protocol failed.");
                Require((await protocol.ConfirmAsync(context, single with { UserId = "target", ColonyId = "target-colony" }, default)).Success, "Prepared protocol failed.");
                var batchCommand = new ClientEventBatchConfirmationCommand("key", "base", Package(), single.Payload, new[] { Entry("é"), Entry("A") });
                Require((await protocol.ConfirmBatchAsync(context, batchCommand, default)).Success && diagnostics == 0, "Batch or lazy diagnostics failed.");
                var missing = new ClashOfRimClientNetworkContext("", "", "", null, null, null, "");
                Require((await protocol.ConfirmAsync(missing, single, default)).ErrorCode == "NotConfigured"
                    && (await protocol.ConfirmBatchAsync(missing, batchCommand, default)).Message == "unconfigured" && calls == 3 && diagnostics == 2, "Missing configuration sent HTTP.");
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static async Task SuccessfulPublication()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-current", "prepared-other" })
        {
            var f = new Fixture();
            var attempt = f.Capture();
            Task work = f.Run(attempt, mode);
            var pending = await f.Ports.Next();
            Require(f.Sync.IsBusy && f.Ports.Published == 0 && f.Accepted == 0 && !work.IsCompleted, "Admission released before main-thread publication.");
            pending();
            await work;
            Require(f.Accepted == 1 && f.Failures.Count == 0 && !f.Sync.IsBusy, "Success did not complete exactly once.");
            bool current = mode != "prepared-other";
            Require(f.Ports.Published == (current ? 1 : 0) && f.Ports.GamePublished == (current ? 1 : 0), "Wrong colony received snapshot publication.");
            Require(f.Ports.Trace.SequenceEqual(current ? new[] { "store", "game", "persist", "accepted" } : new[] { "accepted" }), "Confirmation publication order changed.");
            Require(f.Protocol.Context!.UserId == "alice" && f.Protocol.Context.AuthToken == "auth", "Confirmation borrowed another identity.");
            if (mode == "single") Require(f.Protocol.Single!.IdempotencyKey == "prefix:alice:colony:event:confirmed" && f.Inbox.GroupOf("event") is null, "Single key/group cleanup failed.");
            if (mode == "batch") Require(f.Protocol.Batch!.IdempotencyKey == "prefix:alice:colony:A,a,é:confirmed"
                && f.Protocol.Batch.Applications.Select(e => e.EventId).SequenceEqual(new[] { "é", "a", "A" }), "Ordinal digest or batch order changed.");
            if (mode.StartsWith("prepared")) Require(f.Protocol.Single!.IdempotencyKey == "prepared-key" && f.Protocol.Single.BaseSnapshotId == "target-base"
                && f.Protocol.Single.HttpTimeout == TimeSpan.FromMinutes(7), "Prepared data/timeout was recomputed.");
        }
        var empty = new Fixture(); empty.Protocol.Snapshot = "";
        var task = empty.Run(empty.Capture(), "single"); (await empty.Ports.Next())(); await task;
        Require(empty.Ports.Published == 0 && empty.Accepted == 1, "Empty snapshot prevented domain acknowledgement.");
    }

    private static async Task Failures()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-other" })
        foreach (string error in new[] { "transport", "null", "rejected", "exception" })
        {
            var f = new Fixture(); f.Protocol.Failure = error;
            await f.Run(f.Capture(), mode);
            Require(f.Failures.Single() == (error == "rejected" ? ClientEventConfirmationFailureKind.Rejected
                : error == "exception" ? ClientEventConfirmationFailureKind.Exception : ClientEventConfirmationFailureKind.Transport), "Wrong failure kind: " + error);
            Require(!f.Sync.IsBusy && f.Accepted == 0 && f.Ports.Published == 0 && f.Ports.Queued == 0, "Failure leaked admission or published a snapshot.");
            Require(f.Sync.TryAcquireSnapshot(out var next), "Failure blocked retry admission."); next.Dispose();
        }
    }

    private static async Task ReplacedOwners()
    {
        foreach (string change in new[] { "login", "clear", "colony", "server", "snapshot", "generation", "lease" })
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture(); var attempt = f.Capture();
            Task? work = null; Action? pending = null;
            if (queued) { work = f.Run(attempt, "single"); pending = await f.Ports.Next(); }
            Replace(f, change);
            if (queued) { pending!(); await work!; }
            else await f.Run(attempt, "single");
            Require(f.Accepted == 0 && f.Failures.Count == 0 && f.Ports.Published == 0, "Stale operation published after " + change);
            Require(queued || f.Protocol.Calls == 0, "Expired request sent HTTP after " + change);
        }
        var game = new Fixture(); var task = game.Run(game.Capture(), "single");
        Action callback = await game.Ports.Next(); game.Ports.GameOwner = new(); callback(); await task;
        Require(game.Accepted == 0 && game.Ports.Published == 0 && !game.Sync.IsBusy, "Replaced game received confirmation.");
    }

    private static async Task LateCompletions()
    {
        foreach (string mode in new[] { "single", "batch", "prepared-other" })
        foreach (string change in new[] { "login", "clear", "colony", "generation", "lease" })
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture(); f.Protocol.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = f.Run(f.Capture(), mode);
            Require(f.Protocol.Calls == 1, "Request was not started.");
            Replace(f, change);
            f.Sync.Reset(); Require(f.Sync.TryAcquireSnapshot(out var next), "Replacement could not acquire admission.");
            if (throws) f.Protocol.Hold.SetException(new HttpRequestException("late")); else f.Protocol.Hold.SetResult();
            await work;
            Require(f.Accepted == 0 && f.Failures.Count == 0 && f.Ports.Queued == 0 && next.IsCurrent, "Late response/finally affected replacement after " + change);
            next.Dispose();
        }
        var exit = new Fixture(); var queued = exit.Run(exit.Capture(), "single"); var old = await exit.Ports.Next();
        ClientSessionWork.Reset(); exit.Sync.Reset(); await queued.WaitAsync(TimeSpan.FromSeconds(5));
        old(); Require(exit.Accepted == 0, "Generation cancellation did not drain queued confirmation.");
        var nextOperation = new Fixture(); nextOperation.StartNextOnAccepted = true;
        var accepted = nextOperation.Run(nextOperation.Capture(), "single"); (await nextOperation.Ports.Next())(); await accepted;
        Require(nextOperation.Next!.IsCurrent, "Old finally released callback's new lease."); nextOperation.Next.Dispose();
    }

    private static void RetryPolicy()
    {
        foreach (string code in new[] { nameof(HttpRequestException), nameof(TaskCanceledException), nameof(System.IO.IOException), nameof(ObjectDisposedException) })
            Require(ClientNetworkFailurePolicy.CanRetry(code, null), "Transient exception code became terminal: " + code);
        foreach (string message in new[] { "408 Timeout", "429 Slow down", " 503 Unavailable" })
            Require(ClientNetworkFailurePolicy.CanRetry("HttpError", message), "Transient HTTP error became terminal.");
        foreach (string? message in new[] { null, "", "404 Not found", "401 Unauthorized", "unknown" })
            Require(!ClientNetworkFailurePolicy.CanRetry("HttpError", message), "Terminal HTTP error became retryable.");
        Require(!ClientNetworkFailurePolicy.CanRetry("ValidationFailed", "503")
            && !ClientNetworkFailurePolicy.CanRetry(new InvalidOperationException())
            && ClientNetworkFailurePolicy.CanRetry(new HttpRequestException()), "Retry policy changed its error source semantics.");
        Require(!new ClientEventConfirmationFailure<ModConfirmEventApplicationResponseDto>(ClientEventConfirmationFailureKind.Rejected,
            Rejection: new() { ErrorCode = 503 }).CanRetry, "Business rejection became retryable.");
    }

    private static void Replace(Fixture f, string change)
    {
        switch (change)
        {
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
            case "clear": f.Session.ClearIdentity(); break;
            case "colony": f.Ports.Colony = "new-colony"; break;
            case "server": f.Ports.Server = "http://other"; break;
            case "snapshot": f.Ports.Snapshot = "new-snapshot"; break;
            case "generation": ClientSessionWork.Reset(); f.Sync.Reset(); break;
            case "lease": f.Sync.Reset(); break;
        }
    }

    private sealed class Fixture
    {
        internal readonly Ports Ports = new();
        internal readonly Protocol Protocol = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventConfirmation Confirmation;
        internal readonly List<ClientEventConfirmationFailureKind> Failures = new();
        internal int Accepted;
        internal bool StartNextOnAccepted;
        internal ClientSynchronizationCoordinator.Lease? Next;
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Confirmation = new(Session, Ports, new(Ports, Ports), Inbox, Protocol);
            Inbox.ReplaceQueue(new() { DirectlyProcessable = new() { new() { EventId = "event" } } });
        }
        internal ClientEventConfirmation.Attempt Capture()
        {
            Require(Sync.TryAcquireSnapshot(out var lease), "Fixture admission failed.");
            return Confirmation.Capture(lease!);
        }
        internal Task Run(ClientEventConfirmation.Attempt attempt, string mode)
        {
            if (mode == "batch")
            {
                var entries = new[] { Entry("é"), Entry("a"), Entry("A") };
                var command = Confirmation.Batch(attempt, "prefix", Package(), new byte[] { 1 }, entries);
                entries[0].EventId = "mutated";
                return Confirmation.ConfirmBatchAsync(attempt, command, f => Failures.Add(f.Kind), r => "selected", (r, id) =>
                { Require(id == "selected" && Ports.Snapshot == "selected", "Batch selection not published."); OnAccepted(); });
            }
            var single = Confirmation.Single(attempt, "prefix", "event", "source", null, Package(), new byte[] { 1 }, "arrived");
            if (mode.StartsWith("prepared")) single = single with { IdempotencyKey = "prepared-key", BaseSnapshotId = "target-base", HttpTimeout = TimeSpan.FromMinutes(7),
                UserId = mode == "prepared-other" ? "other" : "alice", ColonyId = mode == "prepared-other" ? "other-colony" : "colony" };
            return Confirmation.ConfirmAsync(attempt, single, f => Failures.Add(f.Kind), _ => OnAccepted());
        }
        private void OnAccepted()
        {
            Require(!Sync.IsBusy, "Domain callback cannot continue synchronization.");
            Accepted++; Ports.Trace.Add("accepted");
            if (StartNextOnAccepted) { Require(Sync.TryAcquireSnapshot(out var next), "Callback could not acquire next lease."); Next = next; }
        }
    }

    private sealed class Ports : IClientSessionIdentityStore, IClientSessionLogoutTransport, IClientSessionStreamTransport, IClientSnapshotStore, IClientSnapshotGame
    {
        internal string User = "alice", Auth = "auth", Colony = "colony", Snapshot = "base", Server = "http://localhost";
        internal object GameOwner = new();
        internal int Published, GamePublished, Queued;
        internal readonly List<string> Trace = new();
        private readonly ConcurrentQueue<Action> pending = new();
        private readonly SemaphoreSlim available = new(0);
        public ClashOfRimClientNetworkContext ReadContext() => new(Server, User, Colony, Snapshot, null, null, Auth);
        public void Write(string? user, string? displayName, string auth) { User = user ?? ""; Auth = auth; }
        public void Publish(string snapshot, string? token) { Snapshot = snapshot; Published++; Trace.Add("store"); }
        public void Persist() => Trace.Add("persist");
        public IClientSnapshotGameSession BindCurrentGame() => new GameSession(this, GameOwner);
        internal async Task<Action> Next()
        {
            Require(await available.WaitAsync(TimeSpan.FromSeconds(5)), "Confirmation did not queue game work.");
            Require(pending.TryDequeue(out var action), "Queue lost work."); return action!;
        }
        private sealed record GameSession(Ports Ports, object Owner) : IClientSnapshotGameSession
        {
            public bool HasGame => true;
            public string RuntimeSnapshotId => Ports.Snapshot;
            public SnapshotSaveCapture Capture(bool removeRaidBattleSessions) => throw new NotSupportedException();
            public void Publish(string snapshot, string? token, IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
            { Require(candidates.Count == 0, "Confirmation consumed unrelated achievements."); Ports.GamePublished++; Ports.Trace.Add("game"); }
            public void Enqueue(Action action, Action discard)
            { Ports.Queued++; Ports.pending.Enqueue(() => { if (ReferenceEquals(Owner, Ports.GameOwner)) action(); else discard(); }); Ports.available.Release(); }
        }
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id) => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string id, long version, long world, Action<ModSessionStreamEvent> onEvent, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Protocol : IClientEventConfirmationProtocol
    {
        internal string? Failure;
        internal string Snapshot = "accepted";
        internal int Calls;
        internal ClashOfRimClientNetworkContext? Context;
        internal ClientEventConfirmationCommand? Single;
        internal ClientEventBatchConfirmationCommand? Batch;
        internal TaskCompletionSource? Hold;
        public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(
            ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token)
        { Calls++; Context = context; Single = command; if (Hold is not null) await Hold.Task;
          return Result(new ModConfirmEventApplicationResponseDto { AppliedSnapshotId = Snapshot, NextLineageToken = "next", Result = new() { Accepted = Failure != "rejected" } }); }
        public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(
            ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token)
        { Calls++; Context = context; Batch = command; if (Hold is not null) await Hold.Task;
          return Result(new ModConfirmEventApplicationsResponseDto { AppliedSnapshotId = Snapshot, NextLineageToken = "next", Result = new() { Accepted = Failure != "rejected" } }); }
        private ClashOfRimClientNetworkResult<T> Result<T>(T response) => Failure switch
        {
            "transport" => ClashOfRimClientNetworkResult<T>.Failed("HttpError", "503 failure"),
            "null" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new HttpRequestException("failure"),
            _ => ClashOfRimClientNetworkResult<T>.Ok(response)
        };
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string name) => name;
        public void SessionExpired(string message, string? token) => throw new Exception("Unexpected expiry.");
        public void MultipartDisposalFailed(string message) => throw new Exception(message);
    }
    private static ModSnapshotPackageMetadataDto Package() => new() { SnapshotId = "confirmed" };
    private static ModConfirmEventApplicationEntryDto Entry(string id) => new() { EventId = id, SourceEventId = "source", ClientApplicationResult = "arrived" };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
