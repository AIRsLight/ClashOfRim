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
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventReadModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        try
        {
            await ProtocolContracts();
            await ModesAndFailures();
            await SnapshotReplacement();
            await SerializedReads();
            await IdentityReplacement();
            await GenerationExit();
            RefreshCoalescing();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task ProtocolContracts()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var context = new ClashOfRimClientNetworkContext("http://localhost/base", "鐢ㄦ埛-茅", "colony", null, null, null, "auth");
                int calls = 0;
                using var handler = new Handler(async request =>
                {
                    var kind = new[] { ProtocolMessageKind.PullPendingEvents, ProtocolMessageKind.WaitForEvents, ProtocolMessageKind.PullEventDetails }[calls++];
                    Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Event route changed.");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var json = body.RootElement;
                    Require(json.GetProperty("authToken").GetString() == "auth" && json.GetProperty("userId").GetString() == "鐢ㄦ埛-茅"
                        && json.GetProperty("colonyId").GetString() == "colony" && json.GetProperty("currentSnapshotId").GetString() == "", "Event identity or null snapshot mapping changed.");
                    if (kind == ProtocolMessageKind.WaitForEvents)
                        Require(json.GetProperty("knownNotificationVersion").GetInt64() == 9007199254740991 && json.GetProperty("timeoutSeconds").GetInt32() == 15, "Wait cursor/timeout lost precision.");
                    if (kind == ProtocolMessageKind.PullEventDetails)
                        Require(json.GetProperty("eventIds").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "A", "a", "浜嬩欢" }), "Event IDs reordered or culture-folded.");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                });
                using var http = new HttpClient(handler);
                int diagnosticReads = 0;
                var protocol = new ClientEventProtocol(new ClientProtocolTransport(http, context, new Environment()), () => { diagnosticReads++; return "not-configured"; });
                Require((await protocol.PullAsync(context, default)).Success, "Queue protocol failed.");
                Require((await protocol.WaitAsync(context, 9007199254740991, 15, default)).Success, "Wait protocol failed.");
                Require((await protocol.DetailsAsync(context, new[] { "A", "a", "浜嬩欢" }, default)).Success, "Details protocol failed.");
                Require(diagnosticReads == 0, "Successful requests evaluated missing-configuration presentation.");
                var missing = new ClashOfRimClientNetworkContext("http://localhost", "user", "", null, null, null, null);
                Require((await protocol.PullAsync(missing, default)).ErrorCode == "NotConfigured"
                    && (await protocol.WaitAsync(missing, 0, 15, default)).Message == "not-configured"
                    && !(await protocol.DetailsAsync(missing, Array.Empty<string>(), default)).Success && calls == 3 && diagnosticReads == 3, "Missing configuration sent HTTP or lost its diagnostic.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static async Task ModesAndFailures()
    {
        foreach (var kind in Enum.GetValues<ClientEventReadKind>())
        {
            var f = new Fixture();
            f.Inbox.ReplaceQueue(Queue("cached"));
            await f.Reader.ReadAsync(f.Reader.Capture(kind, "reason", 42));
            Require(f.Protocol.Calls.SequenceEqual(kind switch
            {
                ClientEventReadKind.Queue => new[] { "queue" },
                ClientEventReadKind.Wait => new[] { "wait:42:15" },
                ClientEventReadKind.Details => new[] { "details:cached" },
                _ => new[] { "queue", "details:event" }
            }), "Read stages changed for " + kind);
            Require(f.Updates.All(u => u.Request.Kind == kind && u.Request.Reason == "reason" && u.Failure is null), "Successful read lost request metadata.");
            if (kind == ClientEventReadKind.Wait)
                Require(f.Updates.Single().Changed && f.Updates[0].NotificationVersion == 43, "Wait response was not published.");
        }
        foreach (bool details in new[] { false, true })
        foreach (string error in new[] { "http", "null", "reject", "throw" })
        {
            var f = new Fixture();
            f.Inbox.ReplaceQueue(Queue("old-queue"));
            f.Inbox.ReplaceDetails(new[] { new ModEventDetailDto { EventId = "old-detail" } });
            f.Protocol.FailStage = details ? "details" : "queue";
            f.Protocol.Failure = error;
            await f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Refresh));
            var failure = f.Updates.Last();
            Require(failure.Failure?.Kind == (error == "reject" ? ClientEventReadFailureKind.Rejected
                : error == "throw" ? ClientEventReadFailureKind.Exception : ClientEventReadFailureKind.Transport)
                && failure.Stage == (details ? ClientEventReadStage.Details : ClientEventReadStage.Queue), "Failure classified incorrectly: " + error);
            Require(f.Inbox.CopyDetails().Single().EventId == "old-detail" && f.Inbox.CopyQueuedIds().Single() == (details ? "event" : "old-queue"), "Failure corrupted the last successful data.");
            Require(f.Protocol.Calls.Count == (details ? 2 : 1), "Read continued after failed queue.");
            f.Protocol.FailStage = null;
            await f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Refresh)).WaitAsync(TimeSpan.FromSeconds(5));
            Require(f.Inbox.CopyDetails().Single().EventId == "event", "Failed read retained the read gate.");
        }
        var empty = new Fixture();
        empty.Protocol.Queue = _ => Task.FromResult(Ok(new ModPullPendingEventsResponseDto()));
        empty.Inbox.ReplaceDetails(new[] { new ModEventDetailDto { EventId = "diagnostic" } });
        await empty.Reader.ReadAsync(empty.Reader.Capture(ClientEventReadKind.Automatic));
        Require(empty.Protocol.Calls.Count == 1 && empty.Updates.Last().Empty && empty.Inbox.CopyDetails().Count == 1, "Empty queue fetched details or erased cached diagnostics.");
        var copied = new Fixture();
        await copied.Reader.ReadAsync(copied.Reader.Capture(ClientEventReadKind.Refresh));
        copied.Updates.Last().Details![0].Status = "observer";
        Require(copied.Inbox.CopyDetails()[0].Status != "observer", "Read observation exposed mutable inbox state.");
        var waiting = new Fixture();
        waiting.Protocol.WaitChanged = false;
        await waiting.Reader.ReadAsync(waiting.Reader.Capture(ClientEventReadKind.Wait, knownVersion: 42));
        Require(!waiting.Updates.Single().Changed && waiting.Inbox.CopyQueuedIds().Single() == "wait", "Timed-out wait discarded its authoritative queue.");
        foreach (string error in new[] { "http", "null", "reject", "throw" })
        {
            waiting = new Fixture();
            waiting.Protocol.FailStage = "wait";
            waiting.Protocol.Failure = error;
            waiting.Inbox.ReplaceQueue(Queue("old"));
            await waiting.Reader.ReadAsync(waiting.Reader.Capture(ClientEventReadKind.Wait));
            Require(waiting.Updates.Single().Failure is not null && waiting.Inbox.CopyQueuedIds().Single() == "old", "Failed wait published a queue or notification cursor.");
        }
    }

    private static async Task SerializedReads()
    {
        var f = new Fixture();
        var held = NewSignal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
        int queued = 0;
        var snapshots = new List<string?>();
        f.Protocol.Queue = context =>
        {
            snapshots.Add(context.CurrentSnapshotId);
            return ++queued == 1 ? held.Task : Task.FromResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("second") }));
        };
        Task first = f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Automatic));
        Task second = f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Refresh));
        Require(queued == 1 && !second.IsCompleted, "Concurrent reads interleaved queue and detail pipelines.");
        f.Ports.Snapshot = "advanced-while-queued";
        held.SetResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("first") }));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Protocol.Calls.SequenceEqual(new[] { "queue", "queue", "details:second" })
            && f.Inbox.CopyDetails().Single().EventId == "second", "A response used IDs belonging to another refresh.");
        Require(snapshots.SequenceEqual(new[] { "snapshot", "advanced-while-queued" }), "Queued refresh sent the snapshot captured before it acquired admission.");
    }

    private static async Task SnapshotReplacement()
    {
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture();
            var held = NewSignal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
            f.Protocol.Queue = _ => held.Task;
            var request = f.Reader.Capture(ClientEventReadKind.Automatic);
            Task work = f.Reader.ReadAsync(request);
            f.Ports.Snapshot = "replacement";
            f.Inbox.ReplaceQueue(Queue("replacement"));
            if (throws) held.SetException(new InvalidOperationException("old request"));
            else held.SetResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("old") }));
            await work;
            Require(f.Updates.Count == 0 && f.Inbox.CopyQueuedIds().Single() == "replacement",
                "Read response borrowed a later snapshot: " + (throws ? "exception" : "success"));
        }
    }

    private static async Task IdentityReplacement()
    {
        foreach (bool replace in new[] { false, true })
        foreach (bool holdDetails in new[] { false, true })
        {
            var f = new Fixture();
            var queue = NewSignal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
            var details = NewSignal<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>>();
            if (holdDetails) f.Protocol.Detail = _ => details.Task;
            else f.Protocol.Queue = _ => queue.Task;
            var oldRequest = f.Reader.Capture(ClientEventReadKind.Automatic);
            Task old = f.Reader.ReadAsync(oldRequest);
            Task queuedOld = f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Refresh));
            var generation = ClientSessionWork.Token;
            if (replace) f.Session.PublishLogin(new() { AuthToken = "new", SessionId = "new", AuthenticatedUserId = "new-user" });
            else f.Session.ClearIdentity();
            Require(ClientSessionWork.Token == generation, "Test unexpectedly reset the generation.");
            f.Inbox.Reset();
            f.Updates.Clear();
            f.Protocol.Queue = _ => Task.FromResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("new") }));
            f.Protocol.Detail = ids => Task.FromResult(Ok(new ModPullEventDetailsResponseDto { Events = ids.Select(id => new ModEventDetailDto { EventId = id }).ToList() }));
            await f.Reader.ReadAsync(f.Reader.Capture(ClientEventReadKind.Refresh)).WaitAsync(TimeSpan.FromSeconds(5));
            Require(f.Inbox.CopyDetails().Single().EventId == "new", "New identity waited for the old HTTP request.");
            int calls = f.Protocol.Calls.Count, updates = f.Updates.Count;
            queue.TrySetResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("late") }));
            details.TrySetResult(Ok(new ModPullEventDetailsResponseDto { Events = new() { new() { EventId = "late" } } }));
            await Task.WhenAll(old, queuedOld).WaitAsync(TimeSpan.FromSeconds(5));
            bool applied = false;
            Require(!f.Reader.TryApply(oldRequest, () => applied = true) && !applied && f.Updates.Count == updates
                && f.Protocol.Calls.Count == calls && f.Inbox.CopyDetails().Single().EventId == "new", "Old identity published, sent queued HTTP or executed a deferred game action.");
        }
    }

    private static async Task GenerationExit()
    {
        var f = new Fixture();
        var held = NewSignal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
        f.Protocol.Queue = _ => held.Task; // A deliberately uncancellable server response.
        var request = f.Reader.Capture(ClientEventReadKind.Refresh);
        Task old = ClientSessionWork.Run(() => f.Reader.ReadAsync(request));
        await f.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClientSessionWork.Reset();
        f.Inbox.ReplaceQueue(Queue("new"));
        held.SetResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = Queue("old") }));
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Updates.Count == 0 && f.Inbox.CopyQueuedIds().Single() == "new", "Expired generation published a late response.");
    }

    private static void RefreshCoalescing()
    {
        var f = new Fixture();
        var refresh = new ClientEventRefreshQueue(f.Reader);
        var first = refresh.Begin()!;
        Parallel.For(0, 32, _ => Require(refresh.Begin() is null, "Concurrent notification started another refresh."));
        Require(!refresh.TakeDeferred() && refresh.Complete(first) && !refresh.Complete(first), "Merged notifications did not produce exactly one follow-up.");
        var next = refresh.Begin()!;
        refresh.Defer();
        Require(!refresh.TakeDeferred() && refresh.Complete(next) && !refresh.TakeDeferred(), "Deferral duplicated an active follow-up.");
        refresh.Defer();
        Require(refresh.TakeDeferred() && !refresh.TakeDeferred(), "Idle deferral was not consumed exactly once.");
        var old = refresh.Begin()!;
        refresh.Defer();
        f.Session.ClearIdentity();
        Require(!refresh.TakeDeferred(), "Identity change retained a deferred notification.");
        var replacement = refresh.Begin()!;
        refresh.Defer();
        Require(!refresh.Complete(old) && refresh.Complete(replacement), "Old finalizer released or consumed replacement work.");
        var beforeExit = refresh.Begin()!;
        ClientSessionWork.Reset();
        var afterExit = refresh.Begin()!;
        refresh.Defer();
        Require(!refresh.Complete(beforeExit) && refresh.Complete(afterExit), "Generation exit reused old refresh ownership.");
    }

    private sealed class Fixture
    {
        internal readonly ClientEventInbox Inbox = new();
        internal readonly EventReadTestProtocol Protocol = new();
        internal readonly EventModuleTestPorts Ports = new() { Snapshot = "snapshot" };
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventReader Reader;
        internal readonly List<ClientEventReadUpdate> Updates = new();
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Reader = new(Session, Inbox, Protocol, Ports);
            Reader.Updated += Updates.Add;
        }
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
        public void SessionExpired(string message, string? authToken) => throw new InvalidOperationException("Unexpected expiry");
        public void MultipartDisposalFailed(string message) => throw new InvalidOperationException(message);
    }
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ClashOfRimClientNetworkResult<T> Ok<T>(T value) => ClashOfRimClientNetworkResult<T>.Ok(value);
    private static ModEventQueueSummaryDto Queue(string id) => new() { DirectlyProcessable = new() { new() { EventId = id } } };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
