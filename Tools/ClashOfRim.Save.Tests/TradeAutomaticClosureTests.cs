using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class TradeAutomaticClosureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        foreach (string cause in new[] { "expire", "baseline" })
        {
            foreach (string source in new[] { "cancel", "expire", "baseline" }) ConsumedReturn(storage, cause, source);
            foreach (string corruption in new[] { "return-target", "return-items", "return-context", "multiple", "exchange", "memo-party",
                "notification-target", "notification-type", "notification-id", "notification-admin", "notification-failed" })
                CorruptionDoesNotBlockOthers(storage, cause, corruption);
            Interrupted(storage, cause);
            Concurrent(storage, cause);
        }
        foreach (string cause in new[] { "expire", "baseline" })
        {
            SqlFailure(cause);
            StaleNotification(cause);
        }
        Ineligible();
    }

    private static void ConsumedReturn(string storage, string cause, string source) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger, "order");
        var returned = Return(order, source);
        returned = returned with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(returned.Type, returned.IdempotencyKey) };
        ledger.Append(returned); Consume(ledger, returned);
        var notification = Notification(order, cause) with { Payload = ((ServerNotificationEventPayload)Notification(order, cause).Payload) with
            { Title = "original localized title", Message = "original localized message" } };
        notification = notification with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(notification.Type, notification.IdempotencyKey) };
        ledger.Append(notification); Consume(ledger, notification);
        string consumed = JsonSerializer.Serialize(new[] { ledger.Find(returned.EventId), ledger.Find(notification.EventId) });
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); int result = Close(state, cause);
        Require(ledger.ListByType(ServerEventType.ItemDelivery).Count == 1,
            "automatic closure returned escrow twice: " + cause + "/" + source);
        Require(result == 1 && ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == 1 && ledger.ListByType(ServerEventType.ServerNotification).Count == 3
            && consumed == JsonSerializer.Serialize(new[] { ledger.Find(returned.EventId), ledger.Find(notification.EventId) })
            && state.EventNotifications.GetVersion("seller") == 1 && state.EventNotifications.GetVersion("buyer") == 1
            && state.EventNotifications.GetVersion("watcher") == 1, "automatic closure duplicated or rewrote prior refund: " + cause + "/" + source);
        string before = Snapshot(ledger);
        Require(Close(state, cause) == 0 && before == Snapshot(ledger), "automatic closure repeat mutated completed graph");
    });

    private static void CorruptionDoesNotBlockOthers(string storage, string cause, string kind) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Order("bad"); ledger.Append(order);
        var memo = Memo(order, "buyer");
        if (kind == "memo-party") memo = memo with { Target = new("wrong", "wrong-colony") };
        ledger.Append(memo);
        var returned = Return(order, cause); var payload = (ItemDeliveryEventPayload)returned.Payload;
        returned = kind switch
        {
            "return-target" => returned with { Target = new("wrong", "wrong-colony") },
            "return-items" => returned with { Payload = payload with { Items = [] } },
            "return-context" => returned with { TargetContext = new("wrong", "map", 2, EventLandingMode.CenterNear) },
            _ => returned
        };
        ledger.Append(returned);
        if (kind == "multiple") ledger.Append(Return(order, "cancel"));
        if (kind == "exchange") ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, memo.Actor, order.Actor, "exchange", false,
            ((TradeEventPayload)memo.Payload) with { Stage = TradeStage.ServerDropPodExchange }, Now));
        if (kind.StartsWith("notification", StringComparison.Ordinal))
        {
            var notification = Notification(order, cause); var n = (ServerNotificationEventPayload)notification.Payload;
            notification = kind switch
            {
                "notification-target" => notification with { Target = new("wrong", "wrong-colony") },
                "notification-type" => notification with { Type = ServerEventType.Trade },
                "notification-id" => notification with { Payload = n with { NotificationId = "unrelated" } },
                "notification-admin" => notification with { Payload = n with { FromAdministrator = true } },
                _ => notification with { Status = ServerEventStatus.Failed }
            };
            ledger.Append(notification);
        }
        var protectedEvents = ledger.ListAll().Select(item => item.EventId).ToArray();
        string before = JsonSerializer.Serialize(protectedEvents.Select(ledger.Find));
        var healthy = Seed(ledger, "healthy");
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); int result = Close(state, cause);
        Require(result == 1 && ledger.Find(healthy.EventId)!.Status == ServerEventStatus.Cancelled
            && before == JsonSerializer.Serialize(protectedEvents.Select(ledger.Find))
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == (kind == "multiple" ? 3 : 2),
            "automatic closure accepted corrupt evidence or blocked healthy order: " + kind + "/" + cause);
    });

    private static void Interrupted(string storage, string cause) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger, "order");
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, TradeMemoCancellationTests.InterruptedLedger>();
        var fault = (TradeMemoCancellationTests.InterruptedLedger)proxy; fault.Inner = ledger; fault.Fail = true;
        var state = State(proxy); bool failed = false;
        try { Close(state, cause); } catch (IOException) { failed = true; }
        Require(failed && ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == 1 && ledger.ListByType(ServerEventType.ServerNotification).Count == 3
            && state.EventNotifications.GetVersion("seller") == 0, "automatic close did not atomically persist mailbox before interrupted wakeup");
        if (storage != "memory") ledger = Open(storage, root);
        state = State(ledger); string before = Snapshot(ledger);
        Require(Close(state, cause) == 0 && before == Snapshot(ledger), "reopened automatic closure duplicated notifications or refund");
    });

    private static void Concurrent(string storage, string cause) => WithRoot(root =>
    {
        var ledger = Open(storage, root); Seed(ledger, "order"); var state = State(ledger);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Close(state, cause))).ToArray(); Task.WaitAll(tasks);
        Require((cause == "expire" || tasks.Sum(task => task.Result) == 1) && ledger.ListByType(ServerEventType.ItemDelivery).Count == 1
            && ledger.ListByType(ServerEventType.ServerNotification).Count == 3 && state.EventNotifications.GetVersion("seller") == 1,
            "concurrent automatic closures duplicated graph");
    });

    private static void SqlFailure(string cause) => WithRoot(root =>
    {
        var ledger = Open("sqlite", root); Seed(ledger, "order"); var state = State(ledger); string before = Snapshot(ledger);
        void Execute(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("create trigger fail_notification before insert on events when new.type='ServerNotification' begin select raise(abort, 'notification failure'); end;");
        bool failed = false;
        try { Close(state, cause); } catch (SqliteException) { failed = true; }
        Require(failed && before == Snapshot(Open("sqlite", root)) && state.EventNotifications.GetVersion("seller") == 0,
            "notification write failure left committed refund or closed order");
        Execute("drop trigger fail_notification;"); Require(Close(state, cause) == 1, "automatic closure could not retry after rollback");
    });

    private static void StaleNotification(string cause)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var order = Seed(ledger, "order"); var notification = Notification(order, cause); ledger.Append(notification);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, TradeCancellationTests.ChangedLedger>();
        var fault = (TradeCancellationTests.ChangedLedger)proxy; fault.Inner = ledger; fault.Target = notification.EventId;
        Require(Close(State(proxy), cause) == 0 && ledger.Find(order.EventId)!.Status != ServerEventStatus.Cancelled
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == 0, "automatic closure overwrote same-status notification change");
    }

    private static void Ineligible()
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); ledger.Append(Order("recent") with { CreatedAtUtc = Now });
        var state = State(ledger); string before = Snapshot(ledger); Require(Close(state, "expire") == 0 && before == Snapshot(ledger), "recent order expired");
        ledger.Append(Order("old")); before = Snapshot(ledger);
        state = new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
        Require(Close(state, "expire") == 0 && before == Snapshot(ledger), "disabled expiration changed ledger");
        Invoke("ApplyTradeOrderBaselineInvalidations", state, new AdminBaselineSnapshot("admin", "admin-colony", Now,
            new Dictionary<string, float> { ["Steel"] = 1, ["Silver"] = 1 }, []), Now);
        Require(before == Snapshot(ledger), "valid definitions invalidated order");
    }

    private static int Close(ClashOfRimNetworkState state, string cause)
    {
        int before = state.Ledger.ListByType(ServerEventType.Trade).Count(item => item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder } && item.Status == ServerEventStatus.Cancelled);
        if (cause == "expire") Invoke("ApplyTradeOrderExpirations", state, Now);
        else return (int)Invoke("ApplyTradeOrderBaselineInvalidations", state,
            new AdminBaselineSnapshot("admin", "admin-colony", Now, new Dictionary<string, float> { ["Silver"] = 1 }, []), Now)!;
        return state.Ledger.ListByType(ServerEventType.Trade).Count(item => item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder } && item.Status == ServerEventStatus.Cancelled) - before;
    }
    private static object? Invoke(string name, params object?[] args)
    {
        try { return typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static AuthoritativeEvent Order(string key) => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), key, false,
        new TradeEventPayload(key, TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), Now.AddHours(-2));
    private static AuthoritativeEvent Memo(AuthoritativeEvent order, string user)
    {
        var p = (TradeEventPayload)order.Payload;
        return AuthoritativeEventFactory.Create(ServerEventType.Trade, new(user, user + "-colony"), new(user, user + "-colony"), order.EventId + user, false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, 0, AcceptedByUserId: user), Now) with { Status = ServerEventStatus.Recorded };
    }
    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger, string key)
    { var order = Order(key); ledger.Append(order); ledger.Append(Memo(order, "buyer")); ledger.Append(Memo(order, "watcher") with { Status = ServerEventStatus.Cancelled }); return order; }
    private static AuthoritativeEvent Return(AuthoritativeEvent order, string cause) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
        (cause switch { "expire" => "trade-expired-owner-return:", "baseline" => "trade-baseline-invalidated-owner-return:", _ => "trade-cancelled-owner-return:" }) + order.EventId, false,
        new ItemDeliveryEventPayload(((TradeEventPayload)order.Payload).OfferedItems, null, Purpose: cause switch
        { "expire" => ItemDeliveryPurpose.TradeExpiredOwnerReturn, "baseline" => ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn, _ => ItemDeliveryPurpose.TradeCancelledOwnerReturn }), Now, order.TargetContext);
    private static AuthoritativeEvent Notification(AuthoritativeEvent order, string cause)
    {
        string key = (cause == "expire" ? "trade-expired:" : "trade-baseline-invalidated:") + order.EventId + ":seller:seller-colony";
        return AuthoritativeEventFactory.Create(ServerEventType.ServerNotification, new("server"), order.Actor, key, false,
            new ServerNotificationEventPayload(key, "title", "message", ServerNotificationSeverity.Warning, false), Now);
    }
    private static void Consume(IAuthoritativeEventLedger ledger, AuthoritativeEvent item) { ledger.MarkDelivered(item.EventId, "before", Now); ledger.MarkApplied(item.EventId, "after", Now); }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.FromHours(1)));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    { "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")), "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), _ => new InMemoryAuthoritativeEventLedger() };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "cor-auto-close-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
}
