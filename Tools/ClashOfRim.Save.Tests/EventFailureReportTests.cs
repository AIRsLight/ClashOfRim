using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class EventFailureReportTests
{
    private static readonly DateTimeOffset Original = DateTimeOffset.UnixEpoch.AddDays(2);
    private static readonly string[] Kinds = ["gift", "support", "return", "loss", "trade", "self"];

    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in Kinds)
        {
            Guards(kind);
            foreach (bool reopen in new[] { false, true })
            {
                foreach (string fault in new[] { "insert", "update" }) Rollback(kind, fault, reopen);
                InterruptedWake(kind, reopen);
            }
            StaleSource(kind);
        }
        foreach (string storage in new[] { "memory", "file", "sqlite" }) ReplayAndParents(storage);
        if (OperatingSystem.IsWindows()) FileRollback();
        ConflictingNotification();
        ConcurrentReports();
        GiftKeyCannotJoinTrade();
        NotificationIdentityBoundaries();
    }

    private static void Guards(string kind)
    {
        var state = new ClashOfRimNetworkState(); var root = Seed(state.Ledger, kind);
        string before = Snapshot(state.Ledger);
        Require(!Report(state, root, user: root.Actor.UserId, colony: root.Actor.ColonyId!).Result.Accepted,
            "sender reported receiver failure: " + kind);
        Require(!Report(state, root, user: root.Target.UserId, colony: "other-colony").Result.Accepted, "wrong colony accepted");
        Require(!Report(state, root, token: "invalid").Result.Accepted, "invalid auth accepted");
        Require(!Report(state, root, source: "unrelated").Result.Accepted, "unrelated source accepted");
        Require(before == Snapshot(state.Ledger), "invalid report mutated ledger");
        foreach (var status in new[] { ServerEventStatus.AppliedToSnapshot, ServerEventStatus.Cancelled, ServerEventStatus.RejectedByTarget })
        {
            state.Ledger.ChangeStatus(root.EventId, status);
            before = Snapshot(state.Ledger);
            Require(!Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "terminal root was changed: " + status);
        }
        // Validate source even on the duplicate path.
        state.Ledger.ChangeStatus(root.EventId, ServerEventStatus.Failed);
        Require(!Report(state, root, source: "unrelated").Result.Accepted, "failed root bypassed source validation");
        if (kind is "trade" or "self")
        {
            state.Ledger.ChangeStatus(root.EventId, ServerEventStatus.DeliveredToClient);
            state.Ledger.ChangeStatus("trade:order", ServerEventStatus.Cancelled);
            before = Snapshot(state.Ledger);
            Require(!Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "closed order minted a second refund");
        }
    }

    private static void Rollback(string kind, string phase, bool reopen) => WithDirectory(directory =>
    {
        string database = Path.Combine(directory, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open(); var root = Seed(ledger, kind); var state = new ClashOfRimNetworkState(ledger: ledger);
        string before = Snapshot(ledger);
        // Notification failure happens after any trade returns were inserted; root update happens after all notifications.
        string condition = phase == "insert" ? "new.type = 'ServerNotification'" : "new.event_id = $root";
        Sql(database, $"create trigger fail_report before {phase} on events when {condition.Replace("$root", "'" + root.EventId + "'")} "
            + "begin select raise(abort, 'injected report failure'); end;");
        Expect<SqliteException>(() => Report(state, root), "report fault not reached");
        Require(before == Snapshot(ledger) && before == Snapshot(Open()), "failure report partially committed");
        Require(state.EventNotifications.GetVersion("buyer") == 0, "failed batch woke receiver");
        Sql(database, "drop trigger fail_report;");
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        Require(Report(state, root).Result.Accepted, "retry failed");
        VerifyCompleted(state.Ledger, root, kind);
        VerifyCompleted(Open(), root, kind);
    });

    private static void InterruptedWake(string kind, bool reopen) => WithDirectory(directory =>
    {
        string database = Path.Combine(directory, "events.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database); var root = Seed(ledger, kind);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var interruption = (SnapshotSupportDeathTests.InterruptedLedger)proxy; interruption.Inner = ledger;
        interruption.AfterBatch = () => throw new IOException("committed before wake");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<IOException>(() => Report(state, root), "post-commit failure not reached");
        VerifyCompleted(ledger, root, kind);
        Require(state.EventNotifications.GetVersion("buyer") == 0, "interruption occurred after wake");
        // A notification may have already been consumed before the original caller retries.
        var notification = ledger.ListByType(ServerEventType.ServerNotification).First();
        ledger.MarkDelivered(notification.EventId, "base", Original);
        ledger.MarkApplied(notification.EventId, "consumed", Original);
        string completed = Snapshot(ledger);
        interruption.AfterBatch = null;
        if (reopen) state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database));
        var retry = Report(state, root, reason: "different retry reason");
        Require(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && completed == Snapshot(state.Ledger), "retry changed durable failure, refund, or consumed notification");
        foreach (var party in state.Ledger.ListByType(ServerEventType.ServerNotification).Select(item => item.Target.UserId).Distinct())
            Require(state.EventNotifications.GetVersion(party) == 1, "retry omitted original recipient: " + party);
    });

    private static void ReplayAndParents(string storage) => WithDirectory(directory =>
    {
        IAuthoritativeEventLedger ledger = storage switch
        {
            "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(directory, "events.sqlite")),
            "file" => new FileAuthoritativeEventLedger(Path.Combine(directory, "events.json")),
            _ => new InMemoryAuthoritativeEventLedger()
        };
        foreach (string kind in new[] { "return", "loss" })
        {
            var root = Seed(ledger, kind, suffix: kind); var parent = ledger.Find("parent-" + kind)!;
            var state = new ClashOfRimNetworkState(ledger: ledger);
            Require(Report(state, root, source: parent.EventId).Result.Accepted, "known parent assertion rejected");
            Require(Same(parent, ledger.Find(parent.EventId)!), "failure changed the completed parent");
            string before = Snapshot(ledger);
            Require(Report(state, root, source: parent.EventId).Result.Accepted && before == Snapshot(ledger), "replay changed parent or failure");
        }
        // Only a server-recorded order/exchange relationship may be asserted for a trade delivery.
        var trade = Seed(ledger, "trade"); var tradeState = new ClashOfRimNetworkState(ledger: ledger);
        Require(Report(tradeState, trade, source: "trade:exchange").Result.Accepted, "known exchange assertion rejected");
        Require(Report(tradeState, trade, source: "trade:order").Result.Accepted, "known order assertion rejected");
        var exchange = ledger.Find("trade:exchange")!;
        Require(exchange.Status == ServerEventStatus.AppliedToSnapshot && exchange.AppliedSnapshotId == "buyer-paid", "lost deduction evidence");
        // Metadata-only events must not be converted into failed deliveries.
        Require(!Report(tradeState, ledger.Find("trade:order")!, user: "seller", colony: "seller-colony").Result.Accepted, "market order accepted as delivery");
        // The exchange actor applies an exchange, whereas its target receives separate delivery events.
        var pending = exchange with { EventId = "trade:pending", IdempotencyKey = "pending", Status = ServerEventStatus.DeliveredToClient,
            AppliedSnapshotId = null, AppliedAtUtc = null, Payload = ((TradeEventPayload)exchange.Payload) with { TradeId = "trade:second-order" } };
        ledger.Append(ledger.Find("trade:order")! with { EventId = "trade:second-order", IdempotencyKey = "second-order", Status = ServerEventStatus.PendingOfflineDelivery });
        ledger.Append(pending);
        Require(!Report(tradeState, pending, user: "seller", colony: "seller-colony").Result.Accepted, "exchange target reported actor failure");
        Require(Report(tradeState, pending, user: "buyer", colony: "buyer-colony", source: "trade:second-order").Result.Accepted,
            "exchange actor could not report failure");
    });

    private static void StaleSource(string kind)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var root = Seed(ledger, kind);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
        var fault = (SnapshotSupportDeathTests.InterruptedLedger)proxy; fault.Inner = ledger;
        AuthoritativeEvent? concurrentlyUpdated = null;
        fault.BeforeBatch = () => concurrentlyUpdated = ledger.ReportApplicationResult(root.EventId, EventApplicationResultKind.NeedsManualReview, "newer report", null);
        var state = new ClashOfRimNetworkState(ledger: proxy); int count = ledger.ListAll().Count;
        Expect<InvalidOperationException>(() => Report(state, root), "stale source accepted");
        Require(ledger.ListAll().Count == count && Same(concurrentlyUpdated!, ledger.Find(root.EventId)!), "stale report overwrote current source");
    }

    private static void FileRollback() => WithDirectory(directory =>
    {
        string path = Path.Combine(directory, "events.json"); var ledger = new FileAuthoritativeEventLedger(path);
        var root = Seed(ledger, "trade"); var state = new ClashOfRimNetworkState(ledger: ledger); string before = Snapshot(ledger);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { Report(state, root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed && before == Snapshot(ledger) && before == Snapshot(new FileAuthoritativeEventLedger(path)), "file failure poisoned cache or disk");
        }
        Require(Report(state, root).Result.Accepted, "file retry failed");
    });

    private static void ConflictingNotification()
    {
        var state = new ClashOfRimNetworkState(); var root = Seed(state.Ledger, "gift");
        Require(Report(state, root).Result.Accepted, "initial failure rejected");
        // Legacy notification formats without related-event fields remain valid.
        var notice = state.Ledger.ListByType(ServerEventType.ServerNotification).First();
        var payload = (ServerNotificationEventPayload)notice.Payload;
        state.Ledger.ApplyBatch(new LedgerBatch([], [new(notice.EventId, notice.Status, notice.Status, payload,
            payload with { RelatedEventId = null, RelatedEventType = null })]));
        Require(Report(state, root).Result.Accepted, "legacy notification replay rejected");
        var current = state.Ledger.Find(notice.EventId)!;
        state.Ledger.ApplyBatch(new LedgerBatch([], [new(notice.EventId, current.Status, current.Status, current.Payload,
            payload with { RelatedEventId = "unrelated" })]));
        string before = Snapshot(state.Ledger);
        Expect<InvalidOperationException>(() => Report(state, root), "conflicting notification accepted");
        Require(before == Snapshot(state.Ledger), "notification conflict changed ledger");
    }

    private static void ConcurrentReports()
    {
        var state = new ClashOfRimNetworkState(); var root = Seed(state.Ledger, "trade");
        string token = Token(state, "buyer", "buyer-colony");
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() => Report(state, root, reason: "reason " + index, token: token))).ToArray();
        Task.WaitAll(tasks);
        Require(tasks.All(task => task.Result.Result.Accepted) && tasks.Count(task => task.Result.AffectedEventCount > 0) == 1,
            "concurrent reports recalculated failure batches");
        VerifyCompleted(state.Ledger, root, "trade");
    }

    private static void GiftKeyCannotJoinTrade()
    {
        foreach (bool reportGift in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); var root = Seed(state.Ledger, "trade");
            var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("seller", "seller-colony"),
                new("buyer", "buyer-colony"), "trade-completed-owner-delivery:trade:order:spoof:colony", true,
                new ItemDeliveryEventPayload([], "gift"), Original)
                with { Status = reportGift ? ServerEventStatus.DeliveredToClient : ServerEventStatus.AppliedToSnapshot };
            state.Ledger.Append(gift);
            var order = state.Ledger.Find("trade:order")!;
            if (reportGift)
            {
                Require(!Report(state, gift, source: "trade:order").Result.Accepted, "gift key authorized trade source");
                Require(Report(state, gift).Result.Accepted && Same(order, state.Ledger.Find(order.EventId)!),
                    "gift key closed unrelated trade");
            }
            else
            {
                Require(Report(state, root).Result.Accepted && Same(gift, state.Ledger.Find(gift.EventId)!), "trade graph included ordinary gift");
                Require(state.Ledger.ListByType(ServerEventType.ItemDelivery).Count(item => ((ItemDeliveryEventPayload)item.Payload).IsTradeReturn) == 2,
                    "ordinary gift blocked legitimate escrow refunds");
            }
        }
    }

    private static void NotificationIdentityBoundaries()
    {
        var state = new ClashOfRimNetworkState(); var root = Seed(state.Ledger, "gift");
        var sibling = root with { EventId = root.EventId + ":suffix", IdempotencyKey = root.IdempotencyKey + ":suffix" };
        state.Ledger.Append(sibling);
        Require(Report(state, sibling).Result.Accepted && Report(state, root).Result.Accepted, "nested event identity confused report lookup");
        string before = Snapshot(state.Ledger);
        Require(Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "nested event identity confused duplicate lookup");
    }

    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger, string kind, string suffix = "")
    {
        var seller = new EventParty("seller", "seller-colony"); var buyer = new EventParty("buyer", "buyer-colony");
        AuthoritativeEvent Create(ServerEventType type, string key, LedgerEventPayload payload, EventParty? actor = null, EventParty? target = null) =>
            AuthoritativeEventFactory.Create(type, actor ?? seller, target ?? buyer, key, true, payload, Original)
                // Keep this graph as pre-upgrade data; failure handling must preserve its old links.
                with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(type, key), Status = ServerEventStatus.DeliveredToClient };
        AuthoritativeEvent root;
        if (kind is "trade" or "self")
        {
            var offered = new[] { new EventThingReference("steel", "Steel", 10, "seller-base") };
            var paid = new[] { new EventThingReference("silver", "Silver", 20, "buyer-base") };
            ledger.Append(Create(ServerEventType.Trade, "order", new TradeEventPayload("order", TradeStage.MarketOrder, offered, paid, 0), seller, new("server"))
                with { Status = ServerEventStatus.AppliedToSnapshot });
            ledger.Append(Create(ServerEventType.Trade, "exchange", new TradeEventPayload("trade:order",
                kind == "self" ? TradeStage.SelfDeliveryExchange : TradeStage.ServerDropPodExchange, offered, paid, 0), buyer, seller)
                with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "buyer-paid", AppliedAtUtc = Original });
            var observer = new EventParty("observer", "observer-colony");
            ledger.Append(Create(ServerEventType.Trade, "memo", new TradeEventPayload("trade:order", TradeStage.AcceptedMemo, offered, paid, 0), observer, observer));
            root = Create(ServerEventType.ItemDelivery, "trade-completed-acceptor-delivery:trade:order:buyer:buyer-colony",
                new ItemDeliveryEventPayload(offered, null, Purpose: ItemDeliveryPurpose.TradeCompletedAcceptorDelivery));
            ledger.Append(Create(ServerEventType.ItemDelivery, "trade-completed-owner-delivery:trade:order:buyer:buyer-colony",
                new ItemDeliveryEventPayload(paid, null, Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery), buyer, seller));
        }
        else if (kind == "loss")
            root = Create(ServerEventType.Raid, "loss" + suffix, new RaidEventPayload("base", null, null, null, null,
                AttackerLoss: new("parent-" + suffix, "base", ["pawn:lost"], [], "lost")));
        else if (kind is "support" or "return")
            root = Create(ServerEventType.SupportPawn, kind + suffix, new SupportPawnEventPayload("pawn:ally", "base", "Ally", true, null,
                ReturnToSender: kind == "return", SourceEventId: kind == "return" ? "parent-" + suffix : null));
        else root = Create(ServerEventType.ItemDelivery, "gift" + suffix, new ItemDeliveryEventPayload([], "gift"));
        if (kind is "loss" or "return")
            ledger.Append(Create(ServerEventType.SupportPawn, "parent-key-" + suffix, new SupportPawnEventPayload("pawn:ally", "base", "Ally", true, null), buyer, seller)
                with { EventId = "parent-" + suffix, Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "original", AppliedAtUtc = Original });
        ledger.Append(root);
        if (ledger.Find("unrelated") is null)
            ledger.Append(Create(ServerEventType.ItemDelivery, "unrelated", new ItemDeliveryEventPayload([], "other"), new("other"), new("third")) with { EventId = "unrelated" });
        return ledger.Find(root.EventId)!;
    }

    private static void VerifyCompleted(IAuthoritativeEventLedger ledger, AuthoritativeEvent root, string kind)
    {
        Require(ledger.Find(root.EventId)!.Status == (kind == "self" ? ServerEventStatus.Conflict : ServerEventStatus.Failed), "root failure missing");
        Require(ledger.Find("unrelated")!.Status == ServerEventStatus.DeliveredToClient, "unrelated event changed");
        if (kind is "trade" or "self")
        {
            Require(ledger.Find("trade:exchange")!.Status == ServerEventStatus.AppliedToSnapshot
                && ledger.Find("trade:exchange")!.AppliedAtUtc == Original, "applied exchange evidence overwritten");
            Require(ledger.Find("trade:order")!.Status == (kind == "self" ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled), "order closure missing");
            Require(ledger.ListByType(ServerEventType.ItemDelivery).Count(item => ((ItemDeliveryEventPayload)item.Payload).IsTradeReturn)
                == (kind == "self" ? 0 : 2), "refund lost or duplicated");
        }
        Require(ledger.ListByType(ServerEventType.ServerNotification).Count == (kind is "trade" or "self" ? 3 : 2), "notification recipient missing");
    }

    private static ReportEventApplicationFailureResponse Report(ClashOfRimNetworkState state, AuthoritativeEvent root,
        string? source = null, string reason = "original failure", string? user = null, string? colony = null, string? token = null)
    {
        user ??= root.Target.UserId; colony ??= root.Target.ColonyId!;
        token ??= Token(state, user, colony);
        var request = new ReportEventApplicationFailureRequest("request-" + reason, root.EventId, user, colony, "current", reason, source, token);
        try { return (ReportEventApplicationFailureResponse)((IValueHttpResult)typeof(ClashOfRimNetworkServer)
            .GetMethod("ReportEventApplicationFailure", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [request, state])!).Value!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static string Token(ClashOfRimNetworkState state, string user, string colony)
    { var now = DateTimeOffset.UtcNow; state.LoginSessions.TryCreate(user, colony, now, out var session); return state.AuthTokens.Issue(user, user, colony, session, now); }
    private static string Snapshot(IAuthoritativeEventLedger ledger) => string.Join("\n", ledger.ListAll().OrderBy(item => item.EventId)
        .Select(item => JsonSerializer.Serialize(item) + JsonSerializer.Serialize(item.Payload, item.Payload.GetType())));
    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new Exception(message); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-failure-report-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
