using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;
using static EventFailureGraph;
using static EventFailureTestClient;
using static SessionTestAuthentication;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class EventFailureReportTests
{
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
        var state = new ClashOfRimNetworkState(); var graph = new EventFailureGraph(state.Ledger, kind); var root = graph.Root;
        string before = Snapshot(state.Ledger);
        Check(!Report(state, root, user: root.Actor.UserId, colony: root.Actor.ColonyId!).Result.Accepted,
            "sender reported receiver failure: " + kind);
        Check(!Report(state, root, user: root.Target.UserId, colony: "other-colony").Result.Accepted, "wrong colony accepted");
        Check(!Report(state, root, token: "invalid").Result.Accepted, "invalid auth accepted");
        Check(!Report(state, root, source: "unrelated").Result.Accepted, "unrelated source accepted");
        Check(before == Snapshot(state.Ledger), "invalid report mutated ledger");
        foreach (var status in new[] { ServerEventStatus.AppliedToSnapshot, ServerEventStatus.Cancelled, ServerEventStatus.RejectedByTarget })
        {
            state.Ledger.ChangeStatus(root.EventId, status);
            before = Snapshot(state.Ledger);
            Check(!Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "terminal root was changed: " + status);
        }
        // Validate source even on the duplicate path.
        state.Ledger.ChangeStatus(root.EventId, ServerEventStatus.Failed);
        Check(!Report(state, root, source: "unrelated").Result.Accepted, "failed root bypassed source validation");
        if (kind is "trade" or "self")
        {
            state.Ledger.ChangeStatus(root.EventId, ServerEventStatus.DeliveredToClient);
            state.Ledger.ChangeStatus("trade:order", ServerEventStatus.Cancelled);
            before = Snapshot(state.Ledger);
            Check(!Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "closed order minted a second refund");
        }
    }

    private static void Rollback(string kind, string phase, bool reopen) => SqliteTestDirectory.With("cor-failure-report-", directory =>
    {
        string database = Path.Combine(directory, "events.sqlite");
        IAuthoritativeEventLedger Open() => new SqliteAuthoritativeEventLedger(database);
        var ledger = Open(); var graph = new EventFailureGraph(ledger, kind); var root = graph.Root; var state = new ClashOfRimNetworkState(ledger: ledger);
        string before = Snapshot(ledger);
        // Notification failure happens after any trade returns were inserted; root update happens after all notifications.
        string condition = phase == "insert" ? "new.type = 'ServerNotification'" : "new.event_id = $root";
        Sql(database, $"create trigger fail_report before {phase} on events when {condition.Replace("$root", "'" + root.EventId + "'")} "
            + "begin select raise(abort, 'injected report failure'); end;");
        Expect<SqliteException>(() => Report(state, root), "report fault not reached");
        Check(before == Snapshot(ledger) && before == Snapshot(Open()), "failure report partially committed");
        Check(state.EventNotifications.GetVersion("buyer") == 0, "failed batch woke receiver");
        Sql(database, "drop trigger fail_report;");
        if (reopen) state = new ClashOfRimNetworkState(ledger: Open());
        Check(Report(state, root).Result.Accepted, "retry failed");
        graph.AssertCompleted(state.Ledger);
        graph.AssertCompleted(Open());
    });

    private static void InterruptedWake(string kind, bool reopen) => SqliteTestDirectory.With("cor-failure-report-", directory =>
    {
        string database = Path.Combine(directory, "events.sqlite");
        var ledger = new SqliteAuthoritativeEventLedger(database); var graph = new EventFailureGraph(ledger, kind); var root = graph.Root;
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var interruption = (InterruptedEventLedger)proxy; interruption.Inner = ledger;
        interruption.AfterBatch = () => throw new IOException("committed before wake");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        Expect<IOException>(() => Report(state, root), "post-commit failure not reached");
        graph.AssertCompleted(ledger);
        Check(state.EventNotifications.GetVersion("buyer") == 0, "interruption occurred after wake");
        // A notification may have already been consumed before the original caller retries.
        var notification = ledger.ListByType(ServerEventType.ServerNotification).First();
        ledger.MarkDelivered(notification.EventId, "base", Original);
        ledger.MarkApplied(notification.EventId, "consumed", Original);
        string completed = Snapshot(ledger);
        interruption.AfterBatch = null;
        if (reopen) state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database));
        var retry = Report(state, root, reason: "different retry reason");
        Check(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && completed == Snapshot(state.Ledger), "retry changed durable failure, refund, or consumed notification");
        foreach (var party in state.Ledger.ListByType(ServerEventType.ServerNotification).Select(item => item.Target.UserId).Distinct())
            Check(state.EventNotifications.GetVersion(party) == 1, "retry omitted original recipient: " + party);
    });

    private static void ReplayAndParents(string storage) => SqliteTestDirectory.With("cor-failure-report-", directory =>
    {
        IAuthoritativeEventLedger ledger = storage switch
        {
            "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(directory, "events.sqlite")),
            "file" => new FileAuthoritativeEventLedger(Path.Combine(directory, "events.json")),
            _ => new InMemoryAuthoritativeEventLedger()
        };
        foreach (string kind in new[] { "return", "loss" })
        {
            var graph = new EventFailureGraph(ledger, kind, suffix: kind); var root = graph.Root; var parent = ledger.Find("parent-" + kind)!;
            var state = new ClashOfRimNetworkState(ledger: ledger);
            Check(Report(state, root, source: parent.EventId).Result.Accepted, "known parent assertion rejected");
            Check(Same(parent, ledger.Find(parent.EventId)!), "failure changed the completed parent");
            string before = Snapshot(ledger);
            Check(Report(state, root, source: parent.EventId).Result.Accepted && before == Snapshot(ledger), "replay changed parent or failure");
        }
        // Only a server-recorded order/exchange relationship may be asserted for a trade delivery.
        var trade = new EventFailureGraph(ledger, "trade").Root; var tradeState = new ClashOfRimNetworkState(ledger: ledger);
        Check(Report(tradeState, trade, source: "trade:exchange").Result.Accepted, "known exchange assertion rejected");
        Check(Report(tradeState, trade, source: "trade:order").Result.Accepted, "known order assertion rejected");
        var exchange = ledger.Find("trade:exchange")!;
        Check(exchange.Status == ServerEventStatus.AppliedToSnapshot && exchange.AppliedSnapshotId == "buyer-paid", "lost deduction evidence");
        // Metadata-only events must not be converted into failed deliveries.
        Check(!Report(tradeState, ledger.Find("trade:order")!, user: "seller", colony: "seller-colony").Result.Accepted, "market order accepted as delivery");
        // The exchange actor applies an exchange, whereas its target receives separate delivery events.
        var pending = exchange with { EventId = "trade:pending", IdempotencyKey = "pending", Status = ServerEventStatus.DeliveredToClient,
            AppliedSnapshotId = null, AppliedAtUtc = null, Payload = ((TradeEventPayload)exchange.Payload) with { TradeId = "trade:second-order" } };
        ledger.Append(ledger.Find("trade:order")! with { EventId = "trade:second-order", IdempotencyKey = "second-order", Status = ServerEventStatus.PendingOfflineDelivery });
        ledger.Append(pending);
        Check(!Report(tradeState, pending, user: "seller", colony: "seller-colony").Result.Accepted, "exchange target reported actor failure");
        Check(Report(tradeState, pending, user: "buyer", colony: "buyer-colony", source: "trade:second-order").Result.Accepted,
            "exchange actor could not report failure");
        string abandoned = Snapshot(ledger);
        Check(Report(tradeState, pending, user: "buyer", colony: "buyer-colony", source: "trade:second-order").Result.Accepted
            && abandoned == Snapshot(ledger), "legacy exchange without deliveries could not replay its recorded failure");
    });

    private static void StaleSource(string kind)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var graph = new EventFailureGraph(ledger, kind); var root = graph.Root;
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var fault = (InterruptedEventLedger)proxy; fault.Inner = ledger;
        AuthoritativeEvent? concurrentlyUpdated = null;
        fault.BeforeBatch = () => concurrentlyUpdated = ledger.ReportApplicationResult(root.EventId, EventApplicationResultKind.NeedsManualReview, "newer report", null);
        var state = new ClashOfRimNetworkState(ledger: proxy); int count = ledger.ListAll().Count;
        Expect<InvalidOperationException>(() => Report(state, root), "stale source accepted");
        Check(ledger.ListAll().Count == count && Same(concurrentlyUpdated!, ledger.Find(root.EventId)!), "stale report overwrote current source");
    }

    private static void FileRollback() => SqliteTestDirectory.With("cor-failure-report-", directory =>
    {
        string path = Path.Combine(directory, "events.json"); var ledger = new FileAuthoritativeEventLedger(path);
        var graph = new EventFailureGraph(ledger, "trade"); var root = graph.Root; var state = new ClashOfRimNetworkState(ledger: ledger); string before = Snapshot(ledger);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { Report(state, root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && before == Snapshot(ledger) && before == Snapshot(new FileAuthoritativeEventLedger(path)), "file failure poisoned cache or disk");
        }
        Check(Report(state, root).Result.Accepted, "file retry failed");
    });

    private static void ConflictingNotification()
    {
        var state = new ClashOfRimNetworkState(); var graph = new EventFailureGraph(state.Ledger, "gift"); var root = graph.Root;
        Check(Report(state, root).Result.Accepted, "initial failure rejected");
        // Legacy notification formats without related-event fields remain valid.
        var notice = state.Ledger.ListByType(ServerEventType.ServerNotification).First();
        var payload = (ServerNotificationEventPayload)notice.Payload;
        state.Ledger.ApplyBatch(new LedgerBatch([], [new(notice.EventId, notice.Status, notice.Status, payload,
            payload with { RelatedEventId = null, RelatedEventType = null })]));
        Check(Report(state, root).Result.Accepted, "legacy notification replay rejected");
        var current = state.Ledger.Find(notice.EventId)!;
        state.Ledger.ApplyBatch(new LedgerBatch([], [new(notice.EventId, current.Status, current.Status, current.Payload,
            payload with { RelatedEventId = "unrelated" })]));
        string before = Snapshot(state.Ledger);
        Expect<InvalidOperationException>(() => Report(state, root), "conflicting notification accepted");
        Check(before == Snapshot(state.Ledger), "notification conflict changed ledger");
    }

    private static void ConcurrentReports()
    {
        var state = new ClashOfRimNetworkState(); var graph = new EventFailureGraph(state.Ledger, "trade"); var root = graph.Root;
        string token = Token(state, "buyer", "buyer-colony");
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() => Report(state, root, reason: "reason " + index, token: token))).ToArray();
        Task.WaitAll(tasks);
        Check(tasks.All(task => task.Result.Result.Accepted) && tasks.Count(task => task.Result.AffectedEventCount > 0) == 1,
            "concurrent reports recalculated failure batches");
        graph.AssertCompleted(state.Ledger);
    }

    private static void GiftKeyCannotJoinTrade()
    {
        foreach (bool reportGift in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); var graph = new EventFailureGraph(state.Ledger, "trade"); var root = graph.Root;
            var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("seller", "seller-colony"),
                new("buyer", "buyer-colony"), "trade-completed-owner-delivery:trade:order:spoof:colony", true,
                new ItemDeliveryEventPayload([], "gift"), Original)
                with { Status = reportGift ? ServerEventStatus.DeliveredToClient : ServerEventStatus.AppliedToSnapshot };
            state.Ledger.Append(gift);
            var order = state.Ledger.Find("trade:order")!;
            if (reportGift)
            {
                Check(!Report(state, gift, source: "trade:order").Result.Accepted, "gift key authorized trade source");
                Check(Report(state, gift).Result.Accepted && Same(order, state.Ledger.Find(order.EventId)!),
                    "gift key closed unrelated trade");
            }
            else
            {
                Check(Report(state, root).Result.Accepted && Same(gift, state.Ledger.Find(gift.EventId)!), "trade graph included ordinary gift");
                Check(state.Ledger.ListByType(ServerEventType.ItemDelivery).Count(item => ((ItemDeliveryEventPayload)item.Payload).IsTradeReturn) == 2,
                    "ordinary gift blocked legitimate escrow refunds");
            }
        }
    }

    private static void NotificationIdentityBoundaries()
    {
        var state = new ClashOfRimNetworkState(); var graph = new EventFailureGraph(state.Ledger, "gift"); var root = graph.Root;
        var sibling = root with { EventId = root.EventId + ":suffix", IdempotencyKey = root.IdempotencyKey + ":suffix" };
        state.Ledger.Append(sibling);
        Check(Report(state, sibling).Result.Accepted && Report(state, root).Result.Accepted, "nested event identity confused report lookup");
        string before = Snapshot(state.Ledger);
        Check(Report(state, root).Result.Accepted && before == Snapshot(state.Ledger), "nested event identity confused duplicate lookup");
    }

    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
