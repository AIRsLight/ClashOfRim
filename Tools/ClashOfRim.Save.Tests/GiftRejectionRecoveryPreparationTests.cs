using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static partial class GiftRejectionEndpointTests
{
    private static void RecoveryPreparation(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
            "target", "cancelled", "applied", "failed", "delivery", "rejection", "published-revoke", "published-replace" })
            WithRoot(root => VerifyRecovery(new(root, factory), change));
        foreach (bool revoked in new[] { false, true }) WithRoot(root => VerifyRecoveryReplay(new(root, factory), revoked));
        foreach (string bad in new[] { "invalid", "body", "sender", "missing" }) WithRoot(root => VerifyRecoveryIdentity(new(root, factory), bad));
        WithRoot(root =>
        {
            var f = new RecoveryFixture(root, factory); f.Seed();
            var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
            Require(replies.All(r => r.Result.Accepted) && replies.Count(r => r.ReturnEventCreated) == 1
                && replies.Select(r => r.ReturnEventId).Distinct().Count() == 1 && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending,
                "Concurrent gift rejection duplicated recovery or returns.");
            f.AssertReturned(); f.AssertReleased();
        });
        WithRoot(root =>
        {
            var f = new RecoveryFixture(root, factory); f.Seed(prepared: true);
            f.Store.BeforePrepare = () => throw new Exception("Cached gift recovery payload was prepared again.");
            f.AfterPublication = () => f.State.AuthTokens.RevokeForUser("receiver");
            var reply = f.Call();
            Require(!reply.Result.Accepted && reply.ReturnEventId is null && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending
                && Same(f.Source, f.State.Ledger.Find(f.Source.EventId)!) && f.State.Ledger.ListAll().Count == 2,
                "Cached recovery bypassed gift rejection authorization.");
            f.AssertReleased();
        });
    }

    private static void VerifyRecovery(RecoveryFixture f, string change)
    {
        f.Seed(); RejectGiftResponse? reply = null; Exception? failure = null; string? before = null;
        AuthoritativeEvent? competingReturn = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 1, "Gift rejection published before journal preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, At);
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                if (change == "target")
                {
                    using var connection = new SqliteConnection("Data Source=" + f.Database); connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "update events set target_user_id = 'other' where event_id = $id";
                    command.Parameters.AddWithValue("$id", f.Source.EventId); command.ExecuteNonQuery();
                }
                if (change == "cancelled") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.Cancelled);
                if (change == "applied")
                {
                    f.State.Ledger.MarkDelivered(f.Source.EventId, "new-base", At);
                    f.State.Ledger.MarkApplied(f.Source.EventId, "new-base", At);
                }
                if (change == "failed") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.Failed);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Source.EventId, "during-recovery", At);
                if (change == "rejection")
                {
                    var result = f.Call(); Require(result.ReturnEventCreated, "Could not reject gift during preparation.");
                    competingReturn = f.State.Ledger.Find(result.ReturnEventId!)!;
                    f.State.Ledger.MarkDelivered(competingReturn.EventId, "return-base", At);
                    f.State.Ledger.MarkApplied(competingReturn.EventId, "home", At);
                    competingReturn = f.State.Ledger.Find(competingReturn.EventId)!;
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser("receiver");
                    else { f.State.LoginSessions.EndUser("receiver"); f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Require((failure is not null) == failed, "Unexpected gift recovery outcome: " + change + " " + failure);
        if (denied || failed)
        {
            Require(reply?.Result.Accepted != true && reply?.ReturnEventId is null && before == f.Evidence()
                && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Failed or denied rejection changed durable state.");
            if (change == "failure")
            {
                f.Open(); Require(f.Call().ReturnEventCreated, "Gift rejection did not resume after reopening failed preparation.");
                f.AssertReturned(); Require(f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending, "Reopened gift recovery was incomplete.");
            }
        }
        else
        {
            Require(!f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && Same(f.ForeignGift, f.State.Ledger.Find(f.ForeignGift.EventId)!), "Gift rejection did not recover the original intent once.");
            bool rejected = change is "target" or "cancelled" or "applied" or "failed" or "published-revoke" or "published-replace";
            Require(reply is not null && reply.Result.Accepted != rejected, "Gift rejection ignored current authority or source: " + change);
            if (rejected)
            {
                Require(!reply!.ReturnEventCreated && reply.ReturnEventId is null && f.State.Ledger.ListAll().Count == 2
                    && f.State.EventNotifications.GetVersion("sender") == 0, "Rejected request leaked a return or notification.");
                if (change.StartsWith("published"))
                {
                    Require(Same(f.Source, f.State.Ledger.Find(f.Source.EventId)!), "Recovery logout changed the source gift.");
                    f.Open(); Require(f.Call().ReturnEventCreated && f.Store.Writes == 0, "Fresh session did not resume without republishing.");
                    f.AssertReturned();
                }
            }
            else if (competingReturn is null) { Require(reply!.ReturnEventCreated, "New gift return was not created."); f.AssertReturned(); }
            else Require(!reply!.ReturnEventCreated && reply.ReturnEventId == competingReturn.EventId
                && Same(competingReturn, f.State.Ledger.Find(competingReturn.EventId)!), "Recovery overwrote a consumed competing return.");
            if (change == "delivery") Require(f.State.Ledger.Find(f.Source.EventId)!.DeliveredToSnapshotId == "during-recovery", "Gift rejection lost delivery metadata.");
        }
        f.AssertReleased();
    }

    private static void VerifyRecoveryReplay(RecoveryFixture f, bool revoke)
    {
        var first = f.Call(); Require(first.ReturnEventCreated, "Could not seed gift replay.");
        string events = Snapshot(f.State.Ledger); f.Seed(); RejectGiftResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser("receiver"); });
        Require(reply!.Result.Accepted != revoke && !reply.ReturnEventCreated && reply.ReturnEventId == (revoke ? null : first.ReturnEventId)
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke,
            "Gift replay recovered or exposed a receipt after revocation.");
        Require(JsonSerializer.Serialize(f.State.Ledger.ListAll().Where(e => e.EventId != f.ForeignGift.EventId).ToArray()) == events,
            "Gift replay rewrote the source or return."); f.AssertReleased();
    }

    private static void VerifyRecoveryIdentity(RecoveryFixture f, string bad)
    {
        f.Seed(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized or malformed gift request prepared recovery.");
        var request = Request(f.Source, bad == "invalid" ? "invalid" : bad == "sender" ? Token(f.State, "sender", "home") : f.AuthToken);
        if (bad == "body") request = new(f.Source.EventId, "intruder", "away", "base", "first", f.AuthToken);
        if (bad == "missing") request = new(f.Source.EventId, "receiver", "away", "", "first", f.AuthToken);
        var reply = Reject(f.State, request);
        Require(!reply.Result.Accepted && reply.ReturnEventId is null && before == f.Evidence() && f.Store.Writes == 0,
            "Invalid gift rejection recovered pending business."); f.AssertReleased();
    }

    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private sealed class RecoveryFixture
    {
        private readonly string root;
        public string Database => Path.Combine(root, "server.sqlite");
        private string Journal => Path.Combine(root, "intents");
        public SaveSnapshotPackage Package { get; }
        public AuthoritativeEvent ForeignGift { get; }
        public AuthoritativeEvent Source { get; } = Gift();
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        public string AuthToken = "";
        public Action? AfterPublication;
        public RecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory)
        {
            this.root = root; ServerDatabaseMigrator.Migrate(Database); Open(); State.Ledger.Append(Source);
            var package = factory("foreign");
            Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            ForeignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "rejection-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), At);
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.gift-recovery", "recovery", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [new RecoveryPublicationProcessor(() => AfterPublication?.Invoke())] }]));
            Store.Gate = State.RaidSettlementSnapshotMutationGate; AuthToken = Token(State, "receiver", "away"); AfterPublication = null;
        }
        public RejectGiftResponse Call() => Reject(State, Request(Source, AuthToken));
        public void Seed(bool prepared = false)
        {
            var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
            using (State.EnterSnapshotMutation())
            {
                Store.FailPublication = true;
                try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                    new(Package.Envelope.Identity, Package.Envelope, Package.Index, At), ForeignGift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
                catch (IOException) { }
                finally { Store.FailPublication = false; }
            }
            Require(State.SnapshotCommits.HasPending, "Could not seed gift recovery.");
        }
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            players = State.Players.FindByUserId("foreign"), journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(),
            notifications = new[] { State.EventNotifications.GetVersion("sender"), State.EventNotifications.GetVersion("receiver"), State.EventNotifications.GetVersion("recipient") } });
        public void AssertReturned()
        {
            var source = State.Ledger.Find(Source.EventId)!;
            var returned = State.Ledger.FindByIdempotencyKey(Source.IdempotencyKey + ":return");
            Require(source.Status == ServerEventStatus.RejectedByTarget && source.DecisionReason == "first" && returned is not null
                && returned.Actor == Source.Target && returned.Target == Source.Actor
                && ((ItemDeliveryEventPayload)returned.Payload).Items.SequenceEqual(((ItemDeliveryEventPayload)Source.Payload).Items)
                && State.Ledger.ListAll().Count == 3, "Gift return lost the original items, parties or decision.");
        }
        public void AssertReleased() => Task.Run(() =>
        {
            Require(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Gift recovery leaked its mutation gate.");
            Monitor.Exit(State.RaidSettlementSnapshotMutationGate);
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private sealed class RecoveryPublicationProcessor(Action run) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.gift-recovery";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") run(); }
    }
}
