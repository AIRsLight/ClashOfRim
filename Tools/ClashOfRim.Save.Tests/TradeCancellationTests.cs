using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class TradeCancellationTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            foreach (bool reopen in new[] { false, true }) Interrupted(storage, reopen);
            foreach (bool closed in new[] { false, true })
            foreach (string kind in new[] { "cancel", "expire", "baseline" }) Partial(storage, closed, kind);
            foreach (bool closed in new[] { false, true })
            foreach (string kind in new[] { "actor", "target", "items", "context", "purpose", "type", "failed", "rejected", "kind", "message",
                "multiple", "exchange", "orphan-delivery", "failure-return", "memo-party", "memo-items", "memo-applied", "memo-type", "duplicate-memo" })
                Corruption(storage, closed, kind);
            MissingReturn(storage);
            Concurrent(storage);
        }
        foreach (string source in new[] { "order", "memo", "return" }) StaleSource(source);
        if (OperatingSystem.IsWindows()) FileFailure();
    }

    private static void Interrupted(string storage, bool reopen) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, TradeMemoCancellationTests.InterruptedLedger>();
        var fault = (TradeMemoCancellationTests.InterruptedLedger)proxy; fault.Inner = ledger; fault.Fail = true;
        var state = State(proxy); string token = TradeCommandAuthTests.Token(state, "seller");
        bool failed = false;
        try { Cancel(state, order, token); } catch (IOException) { failed = true; }
        Require(failed && ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled
            && state.EventNotifications.GetVersion("buyer") == 0, "owner cancellation did not interrupt after commit");
        fault.Fail = false;
        var returned = ledger.FindByIdempotencyKey(ReturnKey(order, "cancel"))!;
        ledger.MarkDelivered(returned.EventId, "before", DateTimeOffset.UtcNow);
        ledger.MarkApplied(returned.EventId, "after", DateTimeOffset.UtcNow);
        if (reopen) state = State(storage == "memory" ? ledger : Open(storage, root));
        token = TradeCommandAuthTests.Token(state, "seller");
        string before = Snapshot(state.Ledger);
        for (int i = 0; i < 3; i++)
        {
            long seller = state.EventNotifications.GetVersion("seller"), buyer = state.EventNotifications.GetVersion("buyer"), watcher = state.EventNotifications.GetVersion("watcher");
            var response = Cancel(state, order, token);
            Require(response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && response.NotifiedAcceptorCount == 2
                && before == Snapshot(state.Ledger) && state.EventNotifications.GetVersion("seller") == seller + 1
                && state.EventNotifications.GetVersion("buyer") == buyer + 1 && state.EventNotifications.GetVersion("watcher") == watcher + 1,
                "owner cancellation retry lost cancelled participants or rewrote consumed return");
        }
    });

    private static void Partial(string storage, bool closed, string kind) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger);
        if (closed) ledger.ChangeStatus(order.EventId, ServerEventStatus.Cancelled);
        var returned = Return(order, kind);
        returned = returned with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(returned.Type, returned.IdempotencyKey) };
        ledger.Append(returned); ledger.MarkDelivered(returned.EventId, "before", DateTimeOffset.UtcNow);
        ledger.MarkApplied(returned.EventId, "after", DateTimeOffset.UtcNow);
        string consumed = JsonSerializer.Serialize(ledger.Find(returned.EventId));
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); var response = Cancel(state, order, TradeCommandAuthTests.Token(state, "seller"));
        Require(response.Result.Accepted && response.NotifiedAcceptorCount == 2 && ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled
            && ledger.ListAll().Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo }).All(item => item.Status == ServerEventStatus.Cancelled)
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == 1 && consumed == JsonSerializer.Serialize(ledger.Find(returned.EventId)),
            "legacy partial cancellation duplicated refund or failed to close memos: " + kind);
    });

    private static void Corruption(string storage, bool closed, string kind) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Order();
        if (closed) order = order with { Status = ServerEventStatus.Cancelled };
        ledger.Append(order);
        var returned = Return(order, "cancel"); var p = (ItemDeliveryEventPayload)returned.Payload;
        returned = kind switch
        {
            "actor" => returned with { Actor = new("other") }, "target" => returned with { Target = new("other", "other-colony") },
            "items" => returned with { Payload = p with { Items = [] } }, "context" => returned with { TargetContext = new("other", "map", 5, EventLandingMode.CenterNear) },
            "purpose" => returned with { Payload = p with { Purpose = ItemDeliveryPurpose.Gift } }, "type" => returned with { Type = ServerEventType.Trade },
            "failed" => returned with { Status = ServerEventStatus.Failed }, "rejected" => returned with { TargetDecision = TargetEventDecision.Rejected },
            "kind" => returned with { Payload = p with { DeliveryKind = "Forced" } }, "message" => returned with { Payload = p with { Message = "gift" } },
            _ => returned
        };
        ledger.Append(returned);
        var memo = Memo(order, "buyer", "memo"); var mp = (TradeEventPayload)memo.Payload;
        if (closed) memo = memo with { Status = ServerEventStatus.Cancelled };
        memo = kind switch
        {
            "memo-party" => memo with { Target = new("other", "other-colony") }, "memo-items" => memo with { Payload = mp with { OfferedItems = [] } },
            "memo-applied" => memo with { Status = ServerEventStatus.AppliedToSnapshot }, "memo-type" => memo with { Type = ServerEventType.ItemDelivery },
            _ => memo
        };
        ledger.Append(memo);
        if (kind == "duplicate-memo")
        {
            // Even a partially closed parent cannot resolve two active acceptances for the same buyer.
            ledger.ChangeStatus(memo.EventId, ServerEventStatus.Recorded);
            ledger.Append(Memo(order, "buyer", "another-memo"));
        }
        if (kind == "multiple") ledger.Append(Return(order, "expire"));
        if (kind == "exchange") ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, memo.Actor, order.Actor, "exchange", false,
            mp with { Stage = TradeStage.ServerDropPodExchange }, DateTimeOffset.UtcNow));
        if (kind is "orphan-delivery" or "failure-return")
        {
            string key = kind == "orphan-delivery" ? $"trade-completed-acceptor-delivery:{order.EventId}:buyer:buyer-colony" : $"trade-application-failed-owner-return:{order.EventId}";
            ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor, key, false, p, DateTimeOffset.UtcNow));
        }
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); string before = Snapshot(ledger);
        var response = Cancel(state, order, TradeCommandAuthTests.Token(state, "seller"));
        Require(!response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.Conflict && before == Snapshot(ledger)
            && state.EventNotifications.GetVersion("seller") == 0 && state.EventNotifications.GetVersion("buyer") == 0,
            "corrupt owner cancellation accepted: " + kind + "/closed=" + closed);
    });

    private static void MissingReturn(string storage) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger); ledger.ChangeStatus(order.EventId, ServerEventStatus.Cancelled);
        var state = State(ledger); string before = Snapshot(ledger);
        Require(!Cancel(state, order, TradeCommandAuthTests.Token(state, "seller")).Result.Accepted && before == Snapshot(ledger),
            "closed parent invented an unproven refund");
    });

    private static void Concurrent(string storage) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Seed(ledger); var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "seller");
        var calls = Enumerable.Range(0, 12).Select(_ => Task.Run(() => Cancel(state, order, token))).ToArray(); Task.WaitAll(calls);
        Require(calls.All(call => call.Result.Result.Accepted && call.Result.NotifiedAcceptorCount == 2)
            && calls.Count(call => call.Result.Result.ErrorCode == ProtocolErrorCode.None) == 1
            && ledger.ListByType(ServerEventType.ItemDelivery).Count == 1 && state.EventNotifications.GetVersion("buyer") == 12,
            "parallel owner cancellation lost wakeups or duplicated escrow");
    });

    private static void StaleSource(string source)
    {
        var ledger = new InMemoryAuthoritativeEventLedger(); var order = Seed(ledger); var returned = Return(order, "cancel"); ledger.Append(returned);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, ChangedLedger>(); var fault = (ChangedLedger)proxy; fault.Inner = ledger;
        fault.Target = source switch { "order" => order.EventId, "return" => returned.EventId, _ => ledger.FindByIdempotencyKey("memo")!.EventId };
        var state = State(proxy); var result = Cancel(state, order, TradeCommandAuthTests.Token(state, "seller"));
        Require(!result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.Conflict && ledger.Find(order.EventId)!.Status != ServerEventStatus.Cancelled
            && state.EventNotifications.GetVersion("buyer") == 0, "stale source was closed: " + source);
    }

    private static void FileFailure() => WithRoot(root =>
    {
        var ledger = Open("file", root); var order = Seed(ledger); var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "seller"), before = Snapshot(ledger);
        bool failed = false;
        using (var blocked = new FileStream(Path.Combine(root, "events.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Cancel(state, order, token); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed && before == Snapshot(ledger), "failed owner cancellation published file cache");
        }
        Require(before == Snapshot(Open("file", root)) && Cancel(state, order, token).Result.Accepted, "file cancellation failed to retry");
    });

    private static AuthoritativeEvent Order() => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "order", false,
        new TradeEventPayload("order", TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow);
    private static AuthoritativeEvent Memo(AuthoritativeEvent order, string user, string key)
    {
        var p = (TradeEventPayload)order.Payload;
        return AuthoritativeEventFactory.Create(ServerEventType.Trade, new(user, user + "-colony"), new(user, user + "-colony"), key, false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, p.FeeSilver, AcceptedByUserId: user), DateTimeOffset.UtcNow)
            with { Status = ServerEventStatus.Recorded };
    }
    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger)
    {
        var order = Order(); ledger.Append(order); ledger.Append(Memo(order, "buyer", "memo"));
        ledger.Append(Memo(order, "watcher", "old-memo") with { Status = ServerEventStatus.Cancelled }); return order;
    }
    private static string ReturnKey(AuthoritativeEvent order, string kind) => (kind switch
        { "expire" => "trade-expired-owner-return:", "baseline" => "trade-baseline-invalidated-owner-return:", _ => "trade-cancelled-owner-return:" }) + order.EventId;
    private static AuthoritativeEvent Return(AuthoritativeEvent order, string kind) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
        ReturnKey(order, kind), false, new ItemDeliveryEventPayload(((TradeEventPayload)order.Payload).OfferedItems, null, Purpose: kind switch
        { "expire" => ItemDeliveryPurpose.TradeExpiredOwnerReturn, "baseline" => ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn, _ => ItemDeliveryPurpose.TradeCancelledOwnerReturn }), DateTimeOffset.UtcNow, order.TargetContext);
    private static CloseTradeOrderResponse Cancel(ClashOfRimNetworkState state, AuthoritativeEvent order, string token)
    {
        return state.Trades.CancelTradeOrder(new CloseTradeOrderRequest("cancel", order.EventId, new("seller", "seller-colony", "snapshot"), null, token));
    }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    { "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")), "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), _ => new InMemoryAuthoritativeEventLedger() };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "cor-owner-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
    public class ChangedLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!; public string Target = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch))
            {
                var status = Inner.Find(Target)!.Status;
                Inner.MarkDelivered(Target, "changed-snapshot", DateTimeOffset.UtcNow);
                Inner.ChangeStatus(Target, status); // Same status, changed full source evidence.
            }
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }
}
