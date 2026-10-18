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
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class DiplomacyCreationModuleTests
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
                foreach (var kind in Enum.GetValues<ClientDiplomacyKind>())
                foreach (bool hasGame in new[] { true, false }) await Success(kind, hasGame);
            }
            await Admission(); await Failures(); await Ownership(); await FailedOwnership(); await Cancellation(); await Reentry();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task Protocol()
    {
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户-é", "actor-colony", "base", null, null, "auth");
        var command = new ClientDiplomacyCommand("key-é", new("bob", "target"), "PeaceRequest", "message-文化", DateTimeOffset.UnixEpoch.AddDays(3));
        int calls = 0, diagnostics = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base/" + ProtocolContractManifest.Find(ProtocolMessageKind.CreateDiplomacyEvent).Route.TrimStart('/'), "Diplomacy route changed.");
            string json = await request.Content!.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<CreateDiplomacyEventRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Check(parsed.Actor.UserId == context.UserId && parsed.Actor.ColonyId == context.ColonyId && parsed.Actor.SnapshotId == "base"
                && parsed.Target.UserId == "bob" && parsed.Target.ColonyId == "target" && parsed.Target.SnapshotId is null
                && parsed.IdempotencyKey == command.IdempotencyKey && parsed.Kind == command.Kind && parsed.Message == command.Message
                && parsed.ExpiresAtUtc == command.ExpiresAtUtc && parsed.AuthToken == "auth", "Diplomacy wire request lost frozen identity or culture-stable fields.");
            using var body = JsonDocument.Parse(json);
            Check(body.RootElement.GetProperty("expiresAtUtc").GetString() == command.ExpiresAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture), "Diplomacy expiry changed with culture.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"eventId\":\"receipt\",\"relationKind\":\"Neutral\"}", Encoding.UTF8, "application/json") };
        }));
        var protocol = new ClientDiplomacyProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()), () => { diagnostics++; return "not-configured"; });
        var result = await protocol.CreateAsync(context, command, default);
        Check(result.Success && result.Response?.EventId == "receipt" && diagnostics == 0, "Protocol did not return receipt.");
        var facade = new ClashOfRimModNetworkClient(http, context);
        Check((await facade.CreateDiplomacyEventAsync(command.IdempotencyKey, "bob", "target", "ignored-target-snapshot", command.Kind, command.Message, command.ExpiresAtUtc)).Success && calls == 2,
            "Legacy facade did not forward to the shared protocol.");
        Check(!(await protocol.CreateAsync(new("", "", "", null, null, null, null), command, default)).Success && calls == 2 && diagnostics == 1, "Unconfigured request reached HTTP.");
    }

    private static async Task Success(ClientDiplomacyKind kind, bool hasGame)
    {
        var f = new Fixture(); f.Ports.HasGame = hasGame; f.Reply.RelationKind = kind is ClientDiplomacyKind.AllianceRequest or ClientDiplomacyKind.PeaceRequest ? null : "nEuTrAl";
        f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new ClientDiplomacyTarget("bob-é", "target", "display-文化"); var before = DateTimeOffset.UtcNow;
        Check(f.Module.Start(kind.ToString().ToLowerInvariant(), target, null, out var work), "Valid diplomacy command was not admitted.");
        await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Selecting another player while awaiting the server cannot retarget an in-flight operation.
        target = new("charlie", "other-colony", "different display");
        var command = f.Command!;
        Check(command.Target.UserId == "bob-é" && command.Kind == kind.ToString() && command.Message == "default:" + kind
            && command.IdempotencyKey.Contains(":alice:colony:bob-é:target:" + kind + ":", StringComparison.Ordinal)
            && f.Context!.CurrentSnapshotId == "base", "Creation captured mutable settings or lost its original context.");
        bool expires = kind is ClientDiplomacyKind.AllianceRequest or ClientDiplomacyKind.PeaceRequest;
        Check(expires ? command.ExpiresAtUtc >= before.AddDays(3) && command.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddDays(3) : command.ExpiresAtUtc is null,
            "Diplomacy expiry differs from invitation semantics.");
        f.Hold.SetResult(); await f.Ports.PumpUntil(work);
        Check(f.Notices.Last().Stage == ClientDiplomacyCreationStage.Accepted && f.Notices.Last().Target.UserId == "bob-é"
            && f.Refreshed == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending && f.Ports.Captures == 0 && f.Ports.Published == 0,
            "Creation did not release admission or incorrectly changed local snapshot state.");
        Check(expires ? f.Relations.Count == 0 : f.Relations.SequenceEqual(new[] { "bob-é:Neutral:" + kind }), "Creation inferred a relation from the requested action instead of the receipt.");
    }

    private static async Task Admission()
    {
        foreach (string change in new[] { "server", "snapshot", "pending", "busy" })
        {
            var f = new Fixture(); ClientSynchronizationCoordinator.Lease? other = null;
            switch (change)
            {
                case "server": f.Ports.Server = ""; break;
                case "snapshot": f.Ports.Snapshot = ""; break;
                case "pending": f.Mutations.Begin(LocalMutationKind.Bank, "unconfirmed"); break;
                case "busy": Check(f.Sync.TryBeginManual(out other), "Cannot seed another operation."); break;
            }
            Check(!f.Start(out var work) && work.IsCompleted && f.Calls == 0 && f.Notices.Count == 1, "Denied creation dispatched work: " + change);
            if (other is not null) { Check(other.IsCurrent, "Denied creation released another lease."); other.Dispose(); }
        }
        foreach (string kind in new[] { "unknown", "0", "99", " WARDECLARATION", "" })
        {
            var f = new Fixture(); Check(!f.Module.Start(kind, new("bob", "target"), null, out _) && !f.Sync.IsBusy && f.Calls == 0, "Invalid action acquired admission.");
        }
        foreach (var target in new[] { new ClientDiplomacyTarget("", "c"), new("bob", ""), new("alice", "colony") })
        {
            var f = new Fixture(); Check(!f.Module.Start("WarDeclaration", target, null, out _) && !f.Sync.IsBusy && f.Calls == 0, "Invalid/self target sent a command.");
        }
        var sameAccount = new Fixture(); Check(sameAccount.Module.Start("WarDeclaration", new("alice", "other-colony"), "custom-文化", out var allowed), "Another colony of the same account was rejected locally.");
        await sameAccount.Ports.PumpUntil(allowed); Check(sameAccount.Command!.Message == "custom-文化", "Custom message changed.");
    }

    private static async Task Failures()
    {
        foreach (string failure in new[] { "transport", "exception", "rejected", "missing-result", "missing-event", "missing-relation", "unknown-relation", "numeric-relation", "undefined-relation" })
        {
            var f = new Fixture();
            switch (failure)
            {
                case "transport": f.Failure = "transport"; break;
                case "exception": f.Failure = "exception"; break;
                case "rejected": f.Reply.Result!.Accepted = false; break;
                case "missing-result": f.Reply.Result = null; break;
                case "missing-event": f.Reply.EventId = null; break;
                case "missing-relation": f.Reply.RelationKind = null; break;
                case "unknown-relation": f.Reply.RelationKind = "Friendly"; break;
                case "numeric-relation": f.Reply.RelationKind = "0"; break;
                case "undefined-relation": f.Reply.RelationKind = "99"; break;
            }
            Check(f.Start(out var work), "Failure case was not started."); await f.Ports.PumpUntil(work);
            Check(f.Relations.Count == 0 && f.Notices.Last().Stage != ClientDiplomacyCreationStage.Accepted && f.Refreshed == 1 && !f.Sync.IsBusy,
                "Incomplete/failed receipt published success or retained admission: " + failure);
        }
        var duplicate = new Fixture(); duplicate.Reply.Result!.ErrorCode = (int)ProtocolErrorCode.DuplicateRequest; duplicate.Reply.RelationKind = "Ally";
        duplicate.Start(out var repeated); await duplicate.Ports.PumpUntil(repeated);
        Check(duplicate.Relations.Single() == "bob:Ally:WarDeclaration", "Duplicate declaration overwrote the server's newer alliance.");
    }

    private static async Task Ownership()
    {
        foreach (string phase in new[] { "started", "send", "queued", "relation", "accepted" })
        foreach (string change in new[] { "user", "auth", "server", "colony", "snapshot", "game", "login", "generation", "lease" })
        {
            var f = new Fixture(); void Change() => f.Replace(change);
            if (phase == "started") f.OnStarted = Change;
            if (phase == "send") f.OnSend = Change;
            if (phase == "queued") f.Ports.BeforeGameAction = Change;
            if (phase == "relation") f.OnRelation = Change;
            if (phase == "accepted") f.OnAccepted = Change;
            f.Start(out var work); await f.Ports.PumpUntil(work);
            int expectedRelations = phase is "relation" or "accepted" ? 1 : 0;
            Check(f.Relations.Count == expectedRelations && f.Refreshed == 0 && !f.Sync.IsBusy
                && f.Notices.Count(n => n.Stage == ClientDiplomacyCreationStage.Accepted) == (phase == "accepted" ? 1 : 0),
                "Obsolete creation crossed an ownership boundary: " + phase + "/" + change
                    + $" calls={f.Calls} relations={f.Relations.Count} refresh={f.Refreshed} busy={f.Sync.IsBusy} notices={string.Join(',', f.Notices.Select(n => n.Stage))}");
        }
    }

    private static async Task FailedOwnership()
    {
        foreach (string failure in new[] { "transport", "exception", "rejected", "incomplete" })
        foreach (string phase in new[] { "send", "queued" })
        foreach (string change in new[] { "user", "auth", "server", "colony", "snapshot", "game", "login", "generation", "lease" })
        {
            var f = new Fixture();
            if (failure == "rejected") f.Reply.Result!.Accepted = false;
            else if (failure == "incomplete") f.Reply.EventId = null;
            else f.Failure = failure;
            if (phase == "send") f.OnSend = () => f.Replace(change);
            else f.Ports.BeforeGameAction = () => f.Replace(change);
            f.Start(out var work); await f.Ports.PumpUntil(work);
            Check(f.Notices.All(n => n.Stage == ClientDiplomacyCreationStage.Started) && f.Relations.Count == 0 && f.Refreshed == 0 && !f.Sync.IsBusy,
                "Obsolete failure changed the replacement session: " + failure + "/" + phase + "/" + change);
        }
    }

    private static async Task Cancellation()
    {
        foreach (bool queued in new[] { false, true })
        {
            var f = new Fixture(); Action? old = null;
            if (!queued) f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Start(out var work);
            if (queued) old = await f.Ports.Next();
            else await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Replace("generation");
            Check(f.Sync.TryBeginManual(out var next), "Replacement session could not acquire admission.");
            await work.WaitAsync(TimeSpan.FromSeconds(5)); old?.Invoke();
            Check(next!.IsCurrent && f.Notices.Count == 1 && f.Relations.Count == 0 && f.Refreshed == 0,
                "Canceled creation published late effects or released new admission.");
            next.Dispose();
        }
    }

    private static async Task Reentry()
    {
        var f = new Fixture(); ClientSynchronizationCoordinator.Lease? next = null;
        f.OnRefresh = () => Check(f.Sync.TryBeginManual(out next), "Refresh could not begin after creation released admission.");
        f.Start(out var work); await f.Ports.PumpUntil(work);
        Check(next?.IsCurrent == true, "Old finally released the newly started operation."); next!.Dispose();
        var obsolete = new Fixture();
        obsolete.OnStarted = () => { obsolete.Sync.Reset(); Check(obsolete.Sync.TryBeginManual(out next), "Cannot start replacement work."); };
        obsolete.Start(out work); await obsolete.Ports.PumpUntil(work);
        Check(next!.IsCurrent && obsolete.Calls == 0, "Obsolete creation sent or released its replacement."); next.Dispose();
        var broken = new Fixture(); broken.OnRelation = () => throw new InvalidOperationException("proxy failed");
        broken.Start(out work); await broken.Ports.PumpUntil(work, _ => { });
        Check(broken.Notices.Last().Stage == ClientDiplomacyCreationStage.Exception && broken.Refreshed == 1 && broken.Calls == 1 && !broken.Sync.IsBusy,
            "Partial relation publication retried the command or failed to refresh/release.");
    }

    private sealed class Fixture : IClientDiplomacyCreationTransport, IClientDiplomacyCreationPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientDiplomacyCreation Module;
        internal ModDiplomacyEventResponseDto Reply = new() { Result = new() { Accepted = true }, EventId = "receipt", RelationKind = "Neutral" };
        internal string? Failure;
        internal int Calls, Refreshed;
        internal ClientDiplomacyCommand? Command;
        internal ClashOfRimClientNetworkContext? Context;
        internal readonly List<ClientDiplomacyCreationNotice> Notices = new();
        internal readonly List<string> Relations = new();
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold;
        internal Action? OnStarted, OnSend, OnRelation, OnAccepted, OnRefresh;
        internal Fixture()
        {
            Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            var transactions = new ClientSnapshotTransaction(Session, Ports, new(Ports, Ports));
            Module = new(new(Session, Sync, Mutations, transactions), transactions, Mutations, this, this);
            Module.RefreshRequired += () => { Refreshed++; OnRefresh?.Invoke(); };
        }
        internal bool Start(out Task work) => Module.Start("WarDeclaration", new("bob", "target"), null, out work);
        public string DefaultMessage(string kind) => "default:" + kind;
        public void Report(ClientDiplomacyCreationNotice notice)
        { Notices.Add(notice); if (notice.Stage == ClientDiplomacyCreationStage.Started) OnStarted?.Invoke(); if (notice.Stage == ClientDiplomacyCreationStage.Accepted) OnAccepted?.Invoke(); }
        public void ApplyRelation(string userId, string relation, string kind) { Relations.Add(userId + ":" + relation + ":" + kind); OnRelation?.Invoke(); }
        public async Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token)
        {
            Calls++; Context = context; Command = command; OnSend?.Invoke(); Sent.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            if (Failure == "exception") throw new HttpRequestException("disconnected");
            return Failure == "transport" ? ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>.Failed("NetworkError", "disconnected")
                : ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>.Ok(Reply);
        }
        internal void Replace(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "snapshot": Ports.Snapshot = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
                // Session exit resets both the work generation and shared admission.
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "lease": Sync.Reset(); break;
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
