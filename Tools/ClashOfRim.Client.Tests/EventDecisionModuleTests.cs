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
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using System.Reflection;
using Verse;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventDecisionModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try { await Contracts(); await AdmissionAndOutcomes(); await OwnedCompletions(); await Reports(); await MainMenuDecisions(); }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task Contracts()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户-é", "colony", null, null, null, "auth");
                int calls = 0, diagnosticReads = 0;
                using var http = new HttpClient(new Handler(async request =>
                {
                    var kind = new[] { ProtocolMessageKind.RejectGift, ProtocolMessageKind.RejectSupportPawn,
                        ProtocolMessageKind.RespondDiplomacyEvent, ProtocolMessageKind.ReportEventApplicationFailure }[calls++];
                    Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Decision route changed.");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var json = body.RootElement;
                    Require(json.GetProperty("userId").GetString() == "用户-é" && json.GetProperty("colonyId").GetString() == "colony"
                        && json.GetProperty("eventId").GetString() == "event-é" && json.GetProperty("reason").GetString() == "reason-原因", "Decision identity or reason changed.");
                    if (kind == ProtocolMessageKind.RespondDiplomacyEvent)
                        Require(json.GetProperty("accepted").GetBoolean(), "Diplomacy decision lost acceptance.");
                    else Require(json.GetProperty("authToken").GetString() == "auth", "Authenticated decision lost its credential.");
                    Require(json.GetProperty("currentSnapshotId").GetString() == (kind == ProtocolMessageKind.ReportEventApplicationFailure ? "report-base" : ""), "Decision snapshot changed.");
                    if (kind == ProtocolMessageKind.ReportEventApplicationFailure)
                        Require(json.GetProperty("idempotencyKey").GetString() == "report-key" && json.GetProperty("sourceEventId").GetString() == "source", "Report lost source or key.");
                    return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }));
                var protocol = new ClientEventDecisionProtocol(new ClientProtocolTransport(http, context, new Environment()), () => { diagnosticReads++; return "not-configured"; });
                Require((await protocol.RejectGiftAsync(context, "event-é", "reason-原因", default)).Success, "Gift decision protocol failed.");
                Require((await protocol.RejectSupportAsync(context, "event-é", "reason-原因", default)).Success, "Support decision protocol failed.");
                Require((await protocol.RespondDiplomacyAsync(context, "event-é", true, "reason-原因", default)).Success, "Diplomacy protocol failed.");
                var report = new ClientEventFailureReport("report-key", "event-é", "source", "report-base", "reason-原因");
                Require((await protocol.ReportFailureAsync(context, report, default)).Success && diagnosticReads == 0, "Report protocol evaluated failure diagnostics.");
                var missing = new ClashOfRimClientNetworkContext(context.ServerBaseUrl, context.UserId, "", null, null, null, context.AuthToken);
                Require(!(await protocol.RejectGiftAsync(missing, "event", null, default)).Success
                    && !(await protocol.RejectSupportAsync(missing, "event", null, default)).Success
                    && !(await protocol.RespondDiplomacyAsync(missing, "event", false, null, default)).Success
                    && !(await protocol.ReportFailureAsync(missing, report, default)).Success && calls == 4 && diagnosticReads == 4, "Missing identity sent a command.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static async Task AdmissionAndOutcomes()
    {
        foreach (var kind in Decisions)
        foreach (string denied in new[] { "identity", "snapshot", "mutation", "busy", "none" })
        {
            var f = new Fixture(); ClientSynchronizationCoordinator.Lease? occupied = null;
            if (denied == "identity") f.Ports.Colony = "";
            if (denied == "snapshot") f.Ports.Snapshot = "";
            if (denied == "mutation") f.Mutations.Begin(LocalMutationKind.Gift, "pending gift");
            if (denied == "busy") f.Sync.TryAcquireSnapshot(out occupied);
            bool started = f.Start(kind, out var task);
            Require(started == (denied == "none"), "Decision admission changed: " + denied);
            if (started)
            {
                var queued = await f.Ports.Next();
                Require(f.Sync.IsBusy && f.Handled.Count == 0, "Decision released admission before owned effects.");
                queued(); await task;
                Require(f.Handled.SequenceEqual(new[] { "event" }) && !f.Sync.IsBusy && f.Notices.Last().Stage == ClientEventDecisionStage.Accepted
                    && f.Ports.Captures == 0 && f.Ports.Published == 0, "Decision completed as a snapshot upload or lost its effects.");
                Require(f.Protocol.Context?.UserId == "alice" && f.Protocol.Context.CurrentSnapshotId == "base", "Decision did not capture identity.");
            }
            else Require(f.Protocol.Calls == 0 && f.Handled.Count == 0 && occupied?.IsCurrent != false, "Denied command changed another lease.");
            occupied?.Dispose();
        }
        foreach (var kind in Decisions)
        foreach (string failure in new[] { "transport", "null", "rejected", "exception" })
        {
            var f = new Fixture(); f.Protocol.Failure = failure;
            Require(f.Start(kind, out var work), "Failure fixture did not start."); await work;
            var expected = failure switch { "rejected" => ClientEventDecisionStage.Rejected, "exception" => ClientEventDecisionStage.Exception, _ => ClientEventDecisionStage.TransportFailed };
            Require(f.Notices.Last().Stage == expected && !f.Sync.IsBusy && f.Handled.Count == 0 && f.Ports.Queued == 0, "Failed decision published or leaked admission.");
        }
        foreach (var kind in new[] { ClientEventDecisionKind.AcceptDiplomacy, ClientEventDecisionKind.RejectDiplomacy })
        {
            var f = new Fixture(); f.Protocol.Relation = "Hostile"; f.Protocol.Duplicate = true;
            f.Start(kind, out var work); (await f.Ports.Next())(); await work;
            Require(f.Relations.Single() == "actor:Hostile:AllianceRequest"
                && f.Notices.Last().Receipt!.Result!.ErrorCode == (int)ProtocolErrorCode.DuplicateRequest,
                "Repeated diplomacy inferred relation from the requested action instead of its receipt.");
        }
    }

    private static async Task OwnedCompletions()
    {
        foreach (var kind in Decisions)
        foreach (string change in new[] { "login", "clear", "server", "colony", "snapshot", "game", "lease", "generation" })
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture();
            if (!queued) f.Protocol.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Start(kind, out var work); await f.Protocol.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Action? pending = queued ? await f.Ports.Next() : null;
            f.Replace(change); f.Sync.Reset(); Require(f.Sync.TryBeginManual(out var replacement), "Replacement could not acquire admission.");
            if (queued) pending!(); else f.Protocol.Hold!.SetResult();
            await work;
            Require(f.Handled.Count == 0 && f.Relations.Count == 0 && f.Notices.All(n => n.Stage == ClientEventDecisionStage.Started)
                && replacement.IsCurrent, "Old decision affected its replacement: " + kind + "/" + change);
            replacement.Dispose();
        }
        var next = new Fixture(); ClientSynchronizationCoordinator.Lease? continuation = null;
        next.OnHandled = () => Require(next.Sync.TryBeginManual(out continuation), "Accepted decision could not continue synchronization.");
        next.Start(ClientEventDecisionKind.RejectSupport, out var task); (await next.Ports.Next())(); await task;
        Require(continuation!.IsCurrent, "Old decision finally released the callback's new lease."); continuation.Dispose();
        var canceled = new Fixture(); canceled.Start(ClientEventDecisionKind.RejectGift, out var pendingTask); var old = await canceled.Ports.Next();
        ClientSessionWork.Reset(); canceled.Sync.Reset(); await pendingTask.WaitAsync(TimeSpan.FromSeconds(5)); old();
        Require(canceled.Handled.Count == 0, "Canceled game wait still applied its decision.");
    }

    private static async Task Reports()
    {
        foreach (string failure in new[] { "none", "transport", "null", "rejected", "exception" })
        {
            var f = new Fixture(); f.Protocol.Failure = failure; f.Mutations.Begin(LocalMutationKind.Gift, "local application failed");
            Require(f.Sync.TryAcquireSnapshot(out var held), "Report fixture admission missing.");
            await f.Decisions.ReportFailureAsync("event", "source", "terminal reason");
            Require(f.Refreshed == (failure == "none" ? 1 : 0) && f.Handled.Count == 0 && held.IsCurrent && f.Mutations.IsPending,
                "Failure reporting disturbed local mutations/admission or refreshed a failed report.");
            Require(f.Protocol.Report!.SnapshotId == "base" && f.Protocol.Report.Reason == "terminal reason"
                && f.Protocol.Report.SourceEventId == "source" && f.Protocol.Report.IdempotencyKey.StartsWith("event-application-failure:alice:event:"), "Report lost its captured intent.");
            held.Dispose();
        }
        foreach (string change in new[] { "login", "clear", "server", "colony", "game", "generation", "snapshot" })
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture(); f.Protocol.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = f.Decisions.ReportFailureAsync("event", null, "reason"); await f.Protocol.Sent.Task;
            f.Replace(change);
            if (throws) f.Protocol.Hold.SetException(new HttpRequestException("late report")); else f.Protocol.Hold.SetResult();
            await work;
            Require(f.Refreshed == (change == "snapshot" && !throws ? 1 : 0)
                && (change == "snapshot" || f.Notices.Count == 0), "Old failure report published into another identity.");
        }
        var empty = new Fixture(); await empty.Decisions.ReportFailureAsync("", null, "reason");
        empty.Ports.Colony = ""; await empty.Decisions.ReportFailureAsync("event", null, "reason");
        Require(empty.Protocol.Calls == 0, "Unconfigured or empty report sent HTTP.");
    }

    private static async Task MainMenuDecisions()
    {
        var previousGame = Current.Game;
        var previousState = Current.ProgramState;
        Current.Game = null; Current.ProgramState = ProgramState.Entry;
        var pump = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
            .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            foreach (var kind in Decisions)
            {
                var f = new Fixture(new ClientSnapshotGame());
                Require(f.Start(kind, out var work), "Menu decision did not start.");
                await f.Protocol.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int attempt = 0; attempt < 100 && !work.IsCompleted; attempt++)
                {
                    pump.Invoke(null, null);
                    await Task.Delay(10);
                }
                Require(work.IsCompleted, "Successful menu decision retained its manual lease in the inactive game queue: " + kind);
                await work;
                Require(!f.Sync.IsBusy && f.Handled.SequenceEqual(new[] { "event" }) && f.Notices.Last().Stage == ClientEventDecisionStage.Accepted,
                    "Menu decision lost its accepted effects or completion.");
            }
        }
        finally
        {
            ClientSessionWork.Reset();
            pump.Invoke(null, null);
            typeof(ClashOfRimGameComponent).GetMethod("RunMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Current.Game = previousGame; Current.ProgramState = previousState;
        }
    }

    private static readonly ClientEventDecisionKind[] Decisions = { ClientEventDecisionKind.RejectGift, ClientEventDecisionKind.RejectSupport,
        ClientEventDecisionKind.AcceptDiplomacy, ClientEventDecisionKind.RejectDiplomacy };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IClientEventDecisionPresentation, IClientEventDecisionRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly Protocol Protocol = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventDecisions Decisions;
        internal readonly List<ClientEventDecisionNotice> Notices = new();
        internal readonly List<string> Handled = new(), Relations = new();
        internal int Refreshed;
        internal Action? OnHandled;
        private readonly Action<Action, Action> enqueue;
        internal Fixture(IClientSnapshotGame? game = null)
        {
            enqueue = game is null ? (action, _) => Ports.Enqueue(action) : new ClientEventDecisionRuntime(static _ => { }).Enqueue;
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Decisions = new(Session, Sync, Mutations, game ?? Ports, Protocol, this, this);
            Decisions.FailureReported += () => Refreshed++;
        }
        internal bool Start(ClientEventDecisionKind kind, out Task work) => kind switch
        {
            ClientEventDecisionKind.RejectGift => Decisions.RejectGift("event", "reason", out work),
            ClientEventDecisionKind.RejectSupport => Decisions.RejectSupport("event", "reason", out work),
            _ => Decisions.RespondDiplomacy("event", "actor", ServerEventType.AllianceRequest, kind == ClientEventDecisionKind.AcceptDiplomacy, "reason", out work)
        };
        public void Report(ClientEventDecisionNotice notice) => Notices.Add(notice);
        public void Enqueue(Action action, Action onDiscard) => enqueue(action, onDiscard);
        public void MarkHandled(string eventId) { Handled.Add(eventId); OnHandled?.Invoke(); }
        public void ApplyDiplomacyRelation(string userId, string? relationKind, ServerEventType eventType) => Relations.Add($"{userId}:{relationKind}:{eventType}");
        internal void Replace(string change)
        {
            switch (change)
            {
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                case "clear": Session.ClearIdentity(); break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "snapshot": Ports.Snapshot = "later"; break;
                case "game": Ports.GameOwner = new(); break;
                case "lease": Sync.Reset(); break;
                case "generation": ClientSessionWork.Reset(); break;
            }
        }
    }
    private sealed class Protocol : IClientEventDecisionProtocol
    {
        internal string? Failure;
        internal string? Relation = "Neutral";
        internal bool Duplicate;
        internal int Calls;
        internal ClashOfRimClientNetworkContext? Context;
        internal ClientEventFailureReport? Report;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold;
        private ModProtocolResponseDto Result() => new() { Accepted = Failure != "rejected", ErrorCode = Duplicate ? (int)ProtocolErrorCode.DuplicateRequest : 0, Message = "processed" };
        public Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
            Send(context, new ModRejectGiftResponseDto { Result = Result(), ReturnEventId = "return", ReturnEventCreated = true });
        public Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
            Send(context, new ModRejectSupportPawnResponseDto { Result = Result(), ReturnEventId = "return", ReturnEventCreated = true });
        public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyAsync(ClashOfRimClientNetworkContext context, string eventId, bool accepted, string? reason, CancellationToken token) =>
            Send(context, new ModDiplomacyEventResponseDto { Result = Result(), RelationKind = Relation });
        public Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportFailureAsync(ClashOfRimClientNetworkContext context, ClientEventFailureReport report, CancellationToken token)
        { Report = report; return Send(context, new ModReportEventApplicationFailureResponseDto { Result = Result(), AffectedEventCount = 3 }); }
        private async Task<ClashOfRimClientNetworkResult<T>> Send<T>(ClashOfRimClientNetworkContext context, T response)
        {
            Calls++; Context = context; Sent.TrySetResult(); if (Hold is not null) await Hold.Task;
            return Failure switch
            {
                "transport" => ClashOfRimClientNetworkResult<T>.Failed("HttpError", "503 unavailable"),
                "null" => ClashOfRimClientNetworkResult<T>.Ok(default!),
                "exception" => throw new HttpRequestException("request failed"),
                _ => ClashOfRimClientNetworkResult<T>.Ok(response)
            };
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
        public string DeserializationFailure(string type) => type;
        public void SessionExpired(string message, string? token) { }
        public void MultipartDisposalFailed(string message) { }
    }
}
