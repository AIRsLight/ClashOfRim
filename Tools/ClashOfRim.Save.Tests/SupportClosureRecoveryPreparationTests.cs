using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static partial class SupportClosureTests
{
    private static void RunRecoveryPreparation(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in Kinds)
        {
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
                "target", "cancelled", "delivery", "closure", "opposite", "published-revoke", "published-replace" })
                WithDirectory(root => VerifyClosureRecovery(new(root, factory, kind), change));
            foreach (bool revoke in new[] { false, true })
                WithDirectory(root => VerifyClosureRecoveryReplay(new(root, factory, kind), revoke));
            foreach (string bad in new[] { "invalid", "body", "owner" })
                WithDirectory(root => VerifyClosureRecoveryIdentity(new(root, factory, kind), bad));
            WithDirectory(root => VerifyConcurrentClosureRecovery(new(root, factory, kind)));
            WithDirectory(root => VerifyPreparedClosureRecovery(new(root, factory, kind)));
        }
    }

    private static void VerifyClosureRecovery(ClosureRecoveryFixture f, string change)
    {
        f.Seed();
        (bool Accepted, bool Created, string? ReturnId) reply = default;
        Exception? failure = null; string? before = null;
        AuthoritativeEvent? competingReturn = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 1, "Support closure published before journal preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, OriginalTime);
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                if (change == "target") Sql(f.Database, "update events set target_user_id = 'other' where event_id = 'source';");
                if (change == "cancelled") f.State.Ledger.ChangeStatus("source", ServerEventStatus.Cancelled);
                if (change == "delivery") f.State.Ledger.MarkDelivered("source", "delivered-during-recovery", OriginalTime);
                if (change is "closure" or "opposite")
                {
                    string competingKind = change == "closure" ? f.Kind : f.Kind == "return" ? "loss" : "return";
                    var closed = Call(f.State, competingKind, token: f.AuthToken, authenticate: false);
                    Require(closed.Accepted && closed.Created, "Could not close support while recovery was preparing.");
                    competingReturn = f.State.Ledger.Find(closed.ReturnId!)!;
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser("userA");
                    else { f.State.LoginSessions.EndUser("userA"); f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Require((failure is not null) == failed, "Unexpected support recovery outcome: " + f.Kind + "/" + change + " " + failure);
        if (denied || failed)
        {
            Require(!reply.Accepted && reply.ReturnId is null && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
                "Denied or failed support closure changed durable state or discarded recovery.");
            if (change == "failure")
            {
                f.Open(); ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                Require(f.Call().Created, "Support closure did not resume after reopening failed preparation.");
                f.AssertClosed(f.Kind);
            }
        }
        else
        {
            Require(!f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && Same(f.Gift, f.State.Ledger.FindByIdempotencyKey("closure-foreign")!), "Support closure did not recover the original intent once.");
            bool rejected = change is "target" or "cancelled" or "published-revoke" or "published-replace" || change == "opposite" && f.Kind == "reject";
            Require(reply.Accepted != rejected, "Support closure ignored current authority or business state: " + f.Kind + "/" + change);
            if (rejected)
            {
                Require(!reply.Created && reply.ReturnId is null, "Rejected support closure exposed a return receipt.");
                if (competingReturn is null) Require(f.State.Ledger.ListAll().Count == 2 && f.State.EventNotifications.GetVersion("owner") == 0,
                    "Rejected support closure created a return or notified its owner.");
                if (change.StartsWith("published")) Require(Same(f.Source, f.State.Ledger.Find("source")!), "Recovery logout changed the support source.");
            }
            else if (competingReturn is null) { Require(reply.Created, "New support closure was not created."); f.AssertClosed(f.Kind); }
            else Require(!reply.Created && reply.ReturnId == competingReturn.EventId, "Competing closure did not retain its original acknowledgement.");
            if (competingReturn is not null) Require(Same(competingReturn, f.State.Ledger.Find(competingReturn.EventId)!), "Recovery replaced the winning closure.");
            if (change == "delivery") Require(f.State.Ledger.Find("source")!.DeliveredToSnapshotId == "delivered-during-recovery", "Recovery discarded delivery metadata.");
            if (change.StartsWith("published"))
            {
                f.Open();
                Require(f.Call().Created && f.Store.Writes == 0 && Same(f.Gift, f.State.Ledger.FindByIdempotencyKey("closure-foreign")!),
                    "A fresh session did not resume support closure without republishing recovered business.");
                f.AssertClosed(f.Kind);
            }
        }
        f.AssertReleased();
    }

    private static void VerifyClosureRecoveryReplay(ClosureRecoveryFixture f, bool revoke)
    {
        var first = f.Call(); Require(first.Created, "Could not seed a support replay.");
        string events = JsonSerializer.Serialize(f.State.Ledger.ListAll());
        f.Seed(); (bool Accepted, bool Created, string? ReturnId) reply = default;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser("userA"); });
        Require(reply.Accepted != revoke && !reply.Created && reply.ReturnId == (revoke ? null : first.ReturnId)
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke,
            "Support replay recovered or returned a receipt after revocation.");
        Require(JsonSerializer.Serialize(f.State.Ledger.ListAll().Where(e => e.EventId != f.Gift.EventId).ToArray()) == events,
            "Support replay rewrote the source or return.");
        f.AssertReleased();
    }

    private static void VerifyClosureRecoveryIdentity(ClosureRecoveryFixture f, string bad)
    {
        f.Seed(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized support request prepared recovery.");
        var reply = Call(f.State, f.Kind, user: bad == "body" ? "intruder" : "userA",
            token: bad == "owner" ? Token(f.State, "owner", "home") : bad == "invalid" ? "invalid" : f.AuthToken, authenticate: false);
        Require(!reply.Accepted && reply.ReturnId is null && before == f.Evidence() && f.Store.Writes == 0, "Wrong support principal recovered pending business.");
        f.AssertReleased();
    }

    private static void VerifyConcurrentClosureRecovery(ClosureRecoveryFixture f)
    {
        f.Seed();
        var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
        Require(replies.All(r => r.Accepted) && replies.Count(r => r.Created) == 1 && replies.Select(r => r.ReturnId).Distinct().Count() == 1
            && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending, "Concurrent support recovery duplicated publication or closures.");
        f.AssertClosed(f.Kind); f.AssertReleased();
    }

    private static void VerifyPreparedClosureRecovery(ClosureRecoveryFixture f)
    {
        f.Seed(prepared: true);
        f.Store.BeforePrepare = () => throw new Exception("Cached recovery payload was prepared again.");
        f.AfterPublication = () => f.State.AuthTokens.RevokeForUser("userA");
        var reply = f.Call();
        Require(!reply.Accepted && reply.ReturnId is null && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending
            && Same(f.Source, f.State.Ledger.Find("source")!) && f.State.Ledger.ListAll().Count == 2,
            "Already prepared recovery skipped its final authority check.");
        f.AssertReleased();
    }

    private sealed class ClosureRecoveryFixture
    {
        private readonly string root;
        public string Kind { get; }
        public string Database => Path.Combine(root, "server.sqlite");
        public string Journal => Path.Combine(root, "intents");
        public SaveSnapshotPackage Package { get; }
        public AuthoritativeEvent Gift { get; }
        public AuthoritativeEvent Source { get; } = SupportClosureTests.Source();
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        public string AuthToken = "";
        public Action? AfterPublication;
        public ClosureRecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
        {
            this.root = root; Kind = kind; ServerDatabaseMigrator.Migrate(Database); Open(); State.Ledger.Append(Source);
            var package = factory("foreign");
            Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            Gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "closure-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), OriginalTime);
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.closure-recovery", "recovery", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [new RecoveryPublicationProcessor(() => AfterPublication?.Invoke())] }]));
            Store.Gate = State.RaidSettlementSnapshotMutationGate; AuthToken = Token(State, "userA"); AfterPublication = null;
        }
        public (bool Accepted, bool Created, string? ReturnId) Call() => SupportClosureTests.Call(State, Kind, token: AuthToken, authenticate: false);
        public void Seed(bool prepared = false)
        {
            var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
            using (State.EnterSnapshotMutation())
            {
                Store.FailPublication = true;
                try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                    new(Package.Envelope.Identity, Package.Envelope, Package.Index, OriginalTime), Gift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
                catch (IOException) { }
                finally { Store.FailPublication = false; }
            }
            Require(State.SnapshotCommits.HasPending, "Could not seed support recovery.");
        }
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            players = State.Players.FindByUserId("foreign"), journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(),
            notifications = new[] { State.EventNotifications.GetVersion("owner"), State.EventNotifications.GetVersion("userA"), State.EventNotifications.GetVersion("recipient") } });
        public void AssertClosed(string kind)
        {
            var ledger = new InMemoryAuthoritativeEventLedger();
            foreach (var item in State.Ledger.ListAll().Where(e => e.EventId != Gift.EventId)) ledger.Append(item);
            Verify(ledger, kind);
        }
        public void AssertReleased() => Task.Run(() =>
        {
            Require(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Support recovery leaked its mutation gate.");
            Monitor.Exit(State.RaidSettlementSnapshotMutationGate);
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private sealed class RecoveryPublicationProcessor(Action run) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.closure-recovery";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") run(); }
    }
}
