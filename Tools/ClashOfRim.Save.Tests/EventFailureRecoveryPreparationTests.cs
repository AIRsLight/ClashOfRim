using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static partial class EventFailureReportTests
{
    private static void RecoveryPreparation(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in Kinds)
        {
            foreach (string invalid in new[] { "token", "body", "fields", "fields-valid" }) WithDirectory(root => VerifyRecoveryIdentity(new(root, factory, kind), invalid));
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
                "published-revoke", "published-replace", "target", "terminal", "report", "delivery" })
                WithDirectory(root => VerifyRecovery(new(root, factory, kind), change));
            foreach (bool revoke in new[] { false, true }) WithDirectory(root => VerifyRecoveryReplay(new(root, factory, kind), revoke));
            WithDirectory(root =>
            {
                var f = new RecoveryFixture(root, factory, kind); f.SeedIntent();
                var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
                Require(replies.All(r => r.Result.Accepted) && replies.Count(r => r.AffectedEventCount > 0) == 1,
                    "Concurrent failure reports recalculated the failure batch.");
                f.AssertRecovered(); VerifyCompleted(f.State.Ledger, f.Source, kind); f.AssertReleased();
            });
            WithDirectory(root =>
            {
                var f = new RecoveryFixture(root, factory, kind); f.SeedIntent(prepared: true);
                f.Store.BeforePrepare = () => throw new Exception("Cached failure report recovery payload was prepared again.");
                f.AfterPublication = () => f.State.AuthTokens.RevokeForUser(f.AuthUser);
                var reply = f.Call();
                Require(!reply.Result.Accepted && reply.AffectedEventCount == 0 && reply.TerminalStatus == "" && f.Business() == f.InitialBusiness,
                    "Cached recovery skipped failure report authorization."); f.AssertRecovered(); f.AssertReleased();
            });
        }
        foreach (string change in new[] { "none", "actor", "published-actor", "logout" })
            WithDirectory(root => VerifyDelegatedRecovery(new(root, factory, "loss", delegated: true), change));
    }

    private static void VerifyRecovery(RecoveryFixture f, string change)
    {
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null; Exception? failure = null; string? before = null, business = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0 && f.Business() == f.InitialBusiness, "Failure report published before recovery preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser(f.AuthUser);
                if (change == "replace") f.State.LoginSessions.TryCreate(f.AuthUser, f.AuthUser + "-colony", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser(f.AuthUser);
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, Original);
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "target") f.ChangeParty("target_user_id");
                if (change == "terminal") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.AppliedToSnapshot);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Source.EventId, "during-recovery", Original);
                if (change == "report")
                {
                    Require(f.Call().AffectedEventCount > 0, "Could not report failure during recovery preparation.");
                    foreach (var notice in f.State.Ledger.ListByType(ServerEventType.ServerNotification))
                    { f.State.Ledger.MarkDelivered(notice.EventId, "before", Original); f.State.Ledger.MarkApplied(notice.EventId, "consumed", Original); }
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser(f.AuthUser);
                    else { f.State.LoginSessions.EndUser(f.AuthUser); f.State.LoginSessions.TryCreate(f.AuthUser, f.AuthUser + "-colony", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence(); business = f.Business();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Require((failure is not null) == failed, "Unexpected failure report recovery: " + f.Kind + "/" + change + " " + failure);
        if (denied || failed)
        {
            Require(reply?.Result.Accepted != true && (reply is null || reply.AffectedEventCount == 0 && reply.TerminalStatus == "")
                && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
                "Rejected failure report changed durable state or returned success fields.");
            if (change == "failure")
            { f.Open(); Require(f.Call().Result.Accepted, "Failure report did not resume after reopening."); f.AssertRecovered(); VerifyCompleted(f.State.Ledger, f.Source, f.Kind); }
        }
        else
        {
            f.AssertRecovered();
            bool rejected = change is "target" or "terminal" or "published-revoke" or "published-replace";
            Require(reply is not null && reply.Result.Accepted != rejected, "Failure report ignored current authority or source: " + f.Kind + "/" + change);
            if (rejected)
            {
                Require(reply!.AffectedEventCount == 0 && f.Business() == business && f.Notifications() == "0/0/0",
                    "Rejected failure report changed its graph or notifications.");
                if (change.StartsWith("published"))
                {
                    Require(reply.TerminalStatus == "", "Revoked failure report exposed the original result.");
                    f.Open(); Require(f.Call().Result.Accepted && f.Store.Writes == 0, "Fresh failure report session republished recovered business.");
                    VerifyCompleted(f.State.Ledger, f.Source, f.Kind);
                }
            }
            else
            {
                VerifyCompleted(f.State.Ledger, f.Source, f.Kind);
                if (change == "report") Require(reply!.AffectedEventCount == 0 && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                    && f.Business() == business, "Repeated failure recalculated refunds or reset consumed notifications.");
                else Require(reply!.AffectedEventCount > 0, "Failure report did not commit its batch.");
                if (change == "delivery") Require(f.State.Ledger.Find(f.Source.EventId)!.DeliveredToSnapshotId == "during-recovery", "Failure report discarded current delivery metadata.");
            }
        }
        f.AssertReleased();
    }

    private static void VerifyRecoveryReplay(RecoveryFixture f, bool revoke)
    {
        Require(f.Call().Result.Accepted, "Could not seed failure report replay."); string before = f.Business();
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser(f.AuthUser); });
        Require(reply!.Result.Accepted != revoke && reply.AffectedEventCount == 0 && f.Business() == before
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke
            && (!revoke || reply.TerminalStatus == ""), "Failure replay recovered or exposed a result after revocation."); f.AssertReleased();
    }

    private static void VerifyRecoveryIdentity(RecoveryFixture f, string invalid)
    {
        f.SeedIntent(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Invalid failure report prepared recovery.");
        // Missing fields take precedence even when the token is invalid.
        var request = new ReportEventApplicationFailureRequest(invalid.StartsWith("fields") ? "" : "key", f.Source.EventId,
            invalid == "body" ? "intruder" : f.Source.Target.UserId, f.Source.Target.ColonyId!, "current", "reason", null,
            invalid is "token" or "fields" ? "invalid" : f.AuthToken);
        ReportEventApplicationFailureResponse reply;
        try { reply = (ReportEventApplicationFailureResponse)((IValueHttpResult)typeof(ClashOfRimNetworkServer)
            .GetMethod("ReportEventApplicationFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [request, f.State])!).Value!; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        Require(!reply.Result.Accepted && reply.AffectedEventCount == 0 && reply.TerminalStatus == ""
            && reply.Result.ErrorCode == (invalid.StartsWith("fields") ? ProtocolErrorCode.ValidationFailed : ProtocolErrorCode.ServerRejected)
            && before == f.Evidence() && f.Store.Writes == 0, "Invalid failure report recovered pending business."); f.AssertReleased();
    }

    private static void VerifyDelegatedRecovery(RecoveryFixture f, string change)
    {
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () =>
            {
                if (change == "actor") f.ChangeParty("actor_user_id");
                if (change == "published-actor") f.AfterPublication = () => f.ChangeParty("actor_user_id");
                if (change == "logout") f.State.LoginSessions.EndUser(f.AuthUser);
                before = f.Evidence();
            });
        Require(reply!.Result.Accepted == (change == "none"), "Failure recovery lost or retained the wrong event-based authorization.");
        if (change is "actor" or "logout") Require(f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending && before == f.Evidence(),
            "Revoked event grant published pending business.");
        else f.AssertRecovered();
        if (change == "none") VerifyCompleted(f.State.Ledger, f.Source, "loss");
        else Require(reply.TerminalStatus == "" && reply.AffectedEventCount == 0 && f.State.Ledger.Find(f.Source.EventId)!.Status == f.Source.Status
            && f.State.Ledger.ListByType(ServerEventType.ServerNotification).Count == 0, "Revoked event grant reported failure or exposed its result.");
        f.AssertReleased();
    }

    private sealed class RecoveryFixture
    {
        private readonly string root;
        private string Database => Path.Combine(root, "server.sqlite");
        private string Journal => Path.Combine(root, "intents");
        public string Kind { get; }
        public string AuthUser { get; private set; } = "buyer";
        public string AuthToken = "";
        public AuthoritativeEvent Source { get; }
        public AuthoritativeEvent ForeignGift { get; }
        public SaveSnapshotPackage Package { get; }
        public string InitialBusiness { get; }
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        public Action? AfterPublication;
        public RecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory, string kind, bool delegated = false)
        {
            this.root = root; Kind = kind; ServerDatabaseMigrator.Migrate(Database); Open(); Source = Seed(State.Ledger, kind);
            AuthUser = delegated ? Source.Actor.UserId : Source.Target.UserId;
            AuthToken = Token(State, AuthUser, AuthUser + "-colony");
            var package = factory("foreign");
            Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            ForeignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "failure-report-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), Original);
            InitialBusiness = Business();
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.failure-recovery", "recovery", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [new RecoveryPublicationProcessor(() => AfterPublication?.Invoke())] }]));
            Store.Gate = State.RaidSettlementSnapshotMutationGate; AuthToken = Token(State, AuthUser, AuthUser + "-colony"); AfterPublication = null;
        }
        public ReportEventApplicationFailureResponse Call() => Report(State, Source, token: AuthToken);
        public void SeedIntent(bool prepared = false)
        {
            var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
            using (State.EnterSnapshotMutation())
            {
                Store.FailPublication = true;
                try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                    new(Package.Envelope.Identity, Package.Envelope, Package.Index, Original), ForeignGift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
                catch (IOException) { }
                finally { Store.FailPublication = false; }
            }
            Require(State.SnapshotCommits.HasPending, "Could not seed failure report recovery.");
        }
        public void ChangeParty(string column)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Database); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = $"update events set {column} = 'other' where event_id = $id";
            command.Parameters.AddWithValue("$id", Source.EventId); command.ExecuteNonQuery();
        }
        public string Business() => string.Join("\n", State.Ledger.ListAll().Where(e => e.IdempotencyKey != "failure-report-foreign").OrderBy(e => e.EventId)
            .Select(e => JsonSerializer.Serialize(e) + JsonSerializer.Serialize(e.Payload, e.Payload.GetType())));
        public string Notifications() => string.Join("/", new[] { "seller", "buyer", "observer" }.Select(State.EventNotifications.GetVersion));
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = Business(),
            foreignGift = State.Ledger.FindByIdempotencyKey("failure-report-foreign"), players = State.Players.FindByUserId("foreign"),
            journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(), notifications = Notifications() });
        public void AssertRecovered() => Require(Store.Writes == 1 && !State.SnapshotCommits.HasPending
            && Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
            && Same(ForeignGift, State.Ledger.Find(ForeignGift.EventId)!), "Failure report did not recover the original intent once.");
        public void AssertReleased() => Task.Run(() =>
        { Require(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Failure report leaked its gate."); Monitor.Exit(State.RaidSettlementSnapshotMutationGate); })
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    private sealed class RecoveryPublicationProcessor(Action run) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.failure-recovery";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") run(); }
    }
}
