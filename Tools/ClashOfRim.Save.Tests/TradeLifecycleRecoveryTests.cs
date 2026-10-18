using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static class TradeLifecycleRecoveryTests
{
    private static readonly string[] Kinds = ["accept", "cancel", "memo-cancel", "complete"];
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in Kinds)
        {
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
                "published-revoke", "published-replace", "terminal", "party", "command", "expired-or-graph" })
                WithRoot(root => Recovery(new(root, factory, kind), change));
            foreach (bool revoked in new[] { false, true }) WithRoot(root => Replay(new(root, factory, kind), revoked));
            foreach (string bad in new[] { "null", "forged", "body" }) WithRoot(root => Identity(new(root, factory, kind), bad));
            foreach (bool validToken in new[] { false, true }) WithRoot(root => ValidationOrder(new(root, factory, kind), validToken));
            WithRoot(root => Concurrent(new(root, factory, kind)));
            WithRoot(root => Prepared(new(root, factory, kind)));
            WithRoot(root => CurrentTrade(new(root, factory, kind, completed: false)));
        }
    }

    private static void Recovery(Fixture f, string change)
    {
        f.Seed(); Reply? reply = null; Exception? failure = null; string? before = null; string? business = null;
        Reply? competitor = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0 && f.Business() == f.InitialBusiness, "Trade command published before recovery preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser(f.User);
                if (change == "replace") f.State.LoginSessions.TryCreate(f.User, f.User + "-colony", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser(f.User);
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, DateTimeOffset.UtcNow);
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                if (change == "terminal") f.State.Ledger.ChangeStatus(f.Order.EventId, ServerEventStatus.Failed);
                if (change == "party")
                {
                    if (f.Kind == "memo-cancel") f.Sql("update events set target_user_id = 'other' where idempotency_key = 'accept'");
                    else f.Sql("update events set actor_user_id = 'buyer' where idempotency_key = 'order'");
                }
                if (change == "command")
                {
                    competitor = f.Call(); Require(competitor.Result.Accepted, "Competing trade command failed.");
                    // An old cancellation receipt must not cancel a later acceptance.
                    if (f.Kind == "memo-cancel") Require(f.Call(kind: "accept", key: "next-accept").Created, "Could not accept again after cancellation.");
                    // An already consumed owner return remains consumed on retry.
                    if (f.Kind == "cancel") foreach (var e in f.Returns())
                    { f.State.Ledger.MarkDelivered(e.EventId, "before-return", DateTimeOffset.UtcNow); f.State.Ledger.MarkApplied(e.EventId, "after-return", DateTimeOffset.UtcNow); }
                }
                if (change == "expired-or-graph")
                {
                    if (f.Kind == "complete") f.State.Ledger.ChangeStatus(f.State.Ledger.FindByIdempotencyKey("exchange")!.EventId, ServerEventStatus.Failed);
                    else f.Sql("update events set created_at_utc = '2000-01-01T00:00:00.0000000+00:00' where idempotency_key = 'order'");
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser(f.User);
                    else { f.State.LoginSessions.EndUser(f.User); f.State.LoginSessions.TryCreate(f.User, f.User + "-colony", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence(); business = f.Business();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Require((failure is not null) == failed, "Unexpected trade recovery outcome: " + f.Kind + "/" + change + " " + failure);
        if (denied || failed)
        {
            Require(reply?.Result.Accepted != true && reply?.MemoId is null && before == f.Evidence()
                && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Denied trade command changed durable state.");
            if (change == "failure")
            { f.Open(); Require(f.Call().Result.Accepted, "Trade command could not resume after reopening."); f.AssertRecovered(); f.AssertSuccess(); }
        }
        else
        {
            f.AssertRecovered();
            bool rejected = change is "terminal" or "party" or "published-revoke" or "published-replace"
                || change == "expired-or-graph" && f.Kind != "cancel";
            Require(reply is not null && reply.Result.Accepted != rejected, "Trade command ignored current authority or business: " + f.Kind + "/" + change);
            if (rejected)
            {
                Require(reply!.MemoId is null && !reply.Created && reply.Notified == 0,
                    "Rejected trade command leaked a receipt or success fields.");
                if (change != "expired-or-graph") Require(f.Business() == business, "Rejected command changed trade records.");
                if (change.StartsWith("published"))
                {
                    Require(f.Notifications() == "0/0/0" && reply.Status == "", "Recovery logout notified trade participants or exposed success fields.");
                    f.Open(); Require(f.Call().Result.Accepted && f.Store.Writes == 0, "Fresh session did not resume without republishing recovery."); f.AssertSuccess();
                }
            }
            else if (competitor is not null)
                Require(!reply!.Created && reply.MemoId == competitor.MemoId && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                    && f.Business() == business, "Competing command overwrote the original receipt or later consumption/acceptance.");
            else f.AssertSuccess();
            if (change == "expired-or-graph" && f.Kind != "complete")
                Require(f.State.Ledger.Find(f.Order.EventId)!.Status == ServerEventStatus.Cancelled && f.Returns().Length == 1,
                    "Trade expiration was skipped or duplicated its escrow return.");
        }
        f.AssertReleased();
    }

    private static void Replay(Fixture f, bool revoked)
    {
        var first = f.Call(); Require(first.Result.Accepted, "Could not seed trade replay."); string before = f.Business();
        f.Seed(); Reply? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoked) f.State.AuthTokens.RevokeForUser(f.User); });
        Require(reply!.Result.Accepted != revoked && !reply.Created && reply.MemoId == (revoked ? null : first.MemoId)
            && f.Store.Writes == (revoked ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoked && f.Business() == before,
            "Trade replay recovered or disclosed its receipt after revocation."); f.AssertReleased();
    }

    private static void Identity(Fixture f, string bad)
    {
        f.Seed(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Invalid trade identity prepared recovery.");
        var reply = f.Call(bad: bad);
        Require(!reply.Result.Accepted && reply.Result.ErrorCode == (bad == "forged" ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.ServerRejected) && reply.MemoId is null
            && f.Evidence() == before && f.Store.Writes == 0, "Invalid trade identity recovered pending business."); f.AssertReleased();
    }

    private static void ValidationOrder(Fixture f, bool validToken)
    {
        f.Seed(); string before = f.Evidence(); Reply? reply = null;
        if (validToken && f.Kind != "complete")
            SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
                () => reply = f.Call(bad: "fields"), () => { });
        else
        {
            f.Store.BeforePrepare = () => throw new Exception("Malformed completion or unauthorized request prepared recovery.");
            reply = f.Call(bad: validToken ? "fields" : "fields-forged");
            Require(before == f.Evidence() && f.State.SnapshotCommits.HasPending, "Request validation moved after recovery.");
        }
        Require(!reply!.Result.Accepted && reply.Result.ErrorCode == (validToken ? ProtocolErrorCode.ValidationFailed : ProtocolErrorCode.SessionUnavailable)
            && reply.MemoId is null && f.Business() == f.InitialBusiness, "Trade validation order or response changed.");
        if (validToken && f.Kind != "complete") f.AssertRecovered(); f.AssertReleased();
    }

    private static void Concurrent(Fixture f)
    {
        f.Seed(); var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
        Require(replies.All(r => r.Result.Accepted) && replies.Count(r => r.Created) == (f.Kind == "complete" ? 0 : 1)
            && replies.Select(r => r.MemoId).Distinct().Count() == 1, "Concurrent trade commands duplicated their receipt.");
        f.AssertRecovered(); f.AssertSuccess(); f.AssertReleased();
    }

    private static void Prepared(Fixture f)
    {
        f.Seed(prepared: true); f.AfterPublication = () => f.State.AuthTokens.RevokeForUser(f.User);
        f.Store.BeforePrepare = () => throw new Exception("Cached trade recovery payload was prepared again.");
        var reply = f.Call();
        Require(!reply.Result.Accepted && reply.MemoId is null && f.Business() == f.InitialBusiness && f.Notifications() == "0/0/0",
            "Cached recovery skipped the final trade authorization check."); f.AssertRecovered(); f.AssertReleased();
    }

    private static void CurrentTrade(Fixture f)
    {
        var graph = new InMemoryAuthoritativeEventLedger(); graph.Append(f.Order);
        TradeCompletionTests.SeedCompleted(graph, f.Order);
        var exchange = graph.FindByIdempotencyKey("exchange")!;
        var records = new[] { exchange }.Concat(graph.ListAll().Where(e => e.EventId != f.Order.EventId && e.EventId != exchange.EventId)).ToArray();
        var changes = f.State.Ledger.ListAll().Select(e => new LedgerStatusChange(e.EventId, e.Status,
            e.EventId == f.Order.EventId ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled, ExpectedEvent: e)).ToArray();
        var package = f.Package with { Envelope = f.Package.Envelope with { Identity = new("buyer", "buyer-colony", "paid-snapshot"), NextLineageToken = "trade-token" } };
        using (f.State.EnterSnapshotMutation())
        {
            f.Store.FailPublication = true;
            try { f.State.SnapshotCommits.CommitTrade(f.Store, f.State.BankLoans, f.State.Ledger, package,
                new(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UtcNow),
                new(new(records, changes), ["seller", "buyer", "watcher"]), (_, _) => { }, _ => { }, _ => { }); }
            catch (IOException) { }
            finally { f.Store.FailPublication = false; }
        }
        Require(f.State.SnapshotCommits.HasPending && f.Business() == f.InitialBusiness, "Could not interrupt current trade fulfillment.");
        Reply? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => Require(f.Store.Writes == 0 && f.Business() == f.InitialBusiness, "Trade graph published before recovery preparation."));
        Require(reply!.Result.Accepted == (f.Kind == "complete") && !reply.Created && reply.MemoId is null
            && f.State.Ledger.Find(f.Order.EventId)!.Status == ServerEventStatus.AppliedToSnapshot && f.Returns().Length == 0
            && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending
            && f.Store.GetLatest("buyer", "buyer-colony")!.Envelope.NextLineageToken == "trade-token",
            "Trade command used an order captured before its own fulfillment recovered: " + f.Kind);
        Require(records.All(e => JsonSerializer.Serialize(e) == JsonSerializer.Serialize(f.State.Ledger.Find(e.EventId)))
            && f.State.Ledger.ListAll().Count == records.Length + changes.Length, "Current trade recovery changed fulfillment evidence.");
        string committed = f.Business(); f.Call();
        Require(f.Store.Writes == 1 && f.Business() == committed, "Retry changed the recovered trade graph."); f.AssertReleased();
    }

    private sealed record Reply(ProtocolResponse Result, string? MemoId, bool Created, string Status = "", int Notified = 0);
    private sealed class Fixture
    {
        private readonly string root;
        private string Database => Path.Combine(root, "server.sqlite");
        private string Journal => Path.Combine(root, "intents");
        public string Kind { get; }
        public string User => Kind is "accept" or "memo-cancel" ? "buyer" : "seller";
        public AuthoritativeEvent Order { get; }
        public AuthoritativeEvent ForeignGift { get; }
        public SaveSnapshotPackage Package { get; }
        public string InitialBusiness { get; }
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        private string token = "";
        public Action? AfterPublication;
        public Fixture(string root, Func<string, SaveSnapshotPackage> factory, string kind, bool completed = true)
        {
            this.root = root; Kind = kind; ServerDatabaseMigrator.Migrate(Database); Open();
            Order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "order", false,
                new TradeEventPayload("order", TradeStage.MarketOrder, [new("steel", "Steel", 10, "seller-base")], [new("silver", "Silver", 20, "buyer-base")], 0), DateTimeOffset.UtcNow);
            State.Ledger.Append(Order);
            if (kind == "complete" && completed) TradeCompletionTests.SeedCompleted(State.Ledger, Order);
            if (kind == "memo-cancel") Require(Call(kind: "accept").Created, "Could not seed active acceptance.");
            var package = factory("foreign");
            Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            ForeignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "trade-command-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), DateTimeOffset.UtcNow);
            InitialBusiness = Business();
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
                serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.FromDays(1)),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.trade-command-recovery", "recovery", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [new PublicationProcessor(() => AfterPublication?.Invoke())] }]));
            Store.Gate = State.RaidSettlementSnapshotMutationGate; token = TradeCommandAuthTests.Token(State, User); AfterPublication = null;
        }
        public Reply Call(string? kind = null, string? key = null, string? bad = null)
        {
            kind ??= Kind; string user = bad == "body" ? "intruder" : User;
            ProtocolIdentity identity = bad == "null" ? null! : new(user, user + "-colony", "base");
            string credential = bad is "forged" or "fields-forged" ? "forged" : token;
            key = bad is "fields" or "fields-forged" ? "" : key ?? (kind == "accept" ? "accept" : "close");
            object result = kind == "accept"
                ? State.Trades.AcceptTradeOrder(new AcceptTradeOrderRequest(key, Order.EventId, identity, false, credential))
                : kind == "complete"
                    ? State.Trades.CompleteTradeOrder(new CloseTradeOrderRequest(key, Order.EventId, identity, "close", credential))
                    : State.Trades.CancelTradeOrder(new CloseTradeOrderRequest(key, Order.EventId, identity, "close", credential));
            return result switch
            {
                AcceptTradeOrderResponse a => new(a.Result, a.MemoEventId, a.MemoCreated),
                CloseTradeOrderResponse c => new(c.Result, null, c.Result.Accepted && c.Result.ErrorCode == ProtocolErrorCode.None, c.TerminalStatus, c.NotifiedAcceptorCount),
                _ => throw new Exception("Unexpected trade reply")
            };
        }
        public void Seed(bool prepared = false)
        {
            var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
            using (State.EnterSnapshotMutation())
            {
                Store.FailPublication = true;
                try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                    new(Package.Envelope.Identity, Package.Envelope, Package.Index, DateTimeOffset.UtcNow), ForeignGift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
                catch (IOException) { }
                finally { Store.FailPublication = false; }
            }
            Require(State.SnapshotCommits.HasPending, "Could not seed trade lifecycle recovery.");
        }
        public void Sql(string sql)
        { using var connection = new SqliteConnection("Data Source=" + Database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string Business() => JsonSerializer.Serialize(State.Ledger.ListAll().Where(e => e.IdempotencyKey != "trade-command-foreign").ToArray());
        public string Notifications() => string.Join("/", new[] { "seller", "buyer", "watcher" }.Select(State.EventNotifications.GetVersion));
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            players = State.Players.FindByUserId("foreign"), journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(), notifications = Notifications() });
        public AuthoritativeEvent[] Returns() => State.Ledger.ListByType(ServerEventType.ItemDelivery)
            .Where(e => e.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeCancelledOwnerReturn or ItemDeliveryPurpose.TradeExpiredOwnerReturn }).ToArray();
        public void AssertRecovered() => Require(Store.Writes == 1 && !State.SnapshotCommits.HasPending
            && Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
            && JsonSerializer.Serialize(ForeignGift) == JsonSerializer.Serialize(State.Ledger.Find(ForeignGift.EventId)), "Trade command did not recover the original intent once.");
        public void AssertSuccess()
        {
            if (Kind == "complete") { Require(Business() == InitialBusiness, "Completion changed its fulfillment graph."); return; }
            if (Kind == "cancel")
            {
                var returned = Returns();
                Require(State.Ledger.Find(Order.EventId)!.Status == ServerEventStatus.Cancelled && returned.Length == 1
                    && returned[0].Target == Order.Actor && ((ItemDeliveryEventPayload)returned[0].Payload).Items.SequenceEqual(((TradeEventPayload)Order.Payload).OfferedItems),
                    "Owner cancellation lost or duplicated escrow."); return;
            }
            var memo = State.Ledger.FindByIdempotencyKey("accept")!;
            Require(State.Ledger.Find(Order.EventId)!.Status == Order.Status && memo.Actor.UserId == "buyer" && memo.Target.UserId == "buyer"
                && ((TradeEventPayload)memo.Payload).TradeId == Order.EventId && ((TradeEventPayload)memo.Payload).OfferedItems.SequenceEqual(((TradeEventPayload)Order.Payload).OfferedItems),
                "Trade acceptance lost its original binding.");
            if (Kind == "memo-cancel") Require(memo.Status == ServerEventStatus.Cancelled
                && State.Ledger.FindByIdempotencyKey("close")!.Payload is TradeEventPayload { Stage: TradeStage.AcceptanceCancelled } receipt
                && receipt.AcceptedMemoEventId == memo.EventId, "Memo cancellation lost its original receipt.");
            else Require(memo.Status == ServerEventStatus.Recorded, "Trade acceptance was not active.");
        }
        public void AssertReleased() => Task.Run(() =>
        { Require(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Trade command leaked its gate."); Monitor.Exit(State.RaidSettlementSnapshotMutationGate); })
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    private sealed class PublicationProcessor(Action run) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.trade-command-recovery";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") run(); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-trade-command-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { run(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
