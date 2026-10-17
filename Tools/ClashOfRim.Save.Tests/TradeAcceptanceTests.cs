using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class TradeAcceptanceTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            KeysAndReplay(storage);
            foreach (string corruption in new[] { "actor", "actor-colony", "target", "target-colony", "faction", "type", "stage",
                "order", "accepted-user", "fee", "offered", "requested", "postage", "exchange", "memo-link", "context", "multiple" })
                ConflictingMemo(storage, corruption);
            foreach (var status in new[] { ServerEventStatus.Cancelled, ServerEventStatus.Failed, ServerEventStatus.RejectedByTarget,
                ServerEventStatus.AppliedToSnapshot, ServerEventStatus.Conflict }) ClosedMemo(storage, status);
            Concurrent(storage);
        }
        SqlFailure();
    }

    private static void KeysAndReplay(string storage) => WithRoot(root =>
    {
        var state = State(Open(storage, root)); var first = Order("first"); var second = Order("second");
        state.Ledger.Append(first); state.Ledger.Append(second);
        string token = TradeCommandAuthTests.Token(state, "buyer");
        var accepted = Accept(state, first, "key", token);
        Require(accepted.Result.Accepted && accepted.MemoCreated, "initial acceptance failed");
        string before = Snapshot(state);
        Conflict(Accept(state, second, "key", token), "key reused for another order");
        Require(before == Snapshot(state), "key reuse changed ledger");
        var secondAccepted = Accept(state, second, "second-key", token);
        Require(secondAccepted.Result.Accepted && secondAccepted.MemoCreated, "second order acceptance failed");
        before = Snapshot(state);
        Conflict(Accept(state, first, "second-key", token), "active memo masked occupied key");
        Require(before == Snapshot(state), "occupied key changed existing memo");
        // Fresh requests can use a different key for the same logical active memo; no alias event is added.
        foreach (string key in new[] { "key", "fresh-key" })
        {
            var retry = Accept(state, first, key, token, snapshot: "new-snapshot");
            Require(retry.Result.Accepted && !retry.MemoCreated && retry.MemoEventId == accepted.MemoEventId,
                "valid acceptance retry changed identity");
        }
        Require(before == Snapshot(state), "valid retries changed stored memo");
        Conflict(Accept(state, first, "key", token, postage: true), "changed postage accepted as replay");
        Require(before == Snapshot(state), "changed request mutated memo");
        // Reopen and retain an old version's event ID and delivery metadata.
        var legacy = Memo(first, "legacy-key") with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(ServerEventType.Trade, "legacy-key") };
        state.Ledger.ChangeStatus(accepted.MemoEventId!, ServerEventStatus.Cancelled);
        state.Ledger.Append(legacy); state.Ledger.MarkDelivered(legacy.EventId, "delivered", DateTimeOffset.UnixEpoch);
        if (storage != "memory") state = State(Open(storage, root));
        token = TradeCommandAuthTests.Token(state, "buyer"); before = Snapshot(state);
        var oldRetry = Accept(state, first, "legacy-key", token);
        Require(oldRetry.Result.Accepted && !oldRetry.MemoCreated && oldRetry.MemoEventId == legacy.EventId
            && before == Snapshot(state), "legacy replay reset identity or lifecycle");
        // Another user's valid session cannot claim the buyer's key.
        string otherToken = TradeCommandAuthTests.Token(state, "other"); before = Snapshot(state);
        Conflict(Accept(state, first, "legacy-key", otherToken, user: "other"), "another user claimed key");
        Require(before == Snapshot(state), "foreign request changed memo");
    });

    private static void ConflictingMemo(string storage, string corruption) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Order("order"); ledger.Append(order);
        var memo = Memo(order, "key"); var payload = (TradeEventPayload)memo.Payload;
        memo = corruption switch
        {
            "actor" => memo with { Actor = memo.Actor with { UserId = "other" } },
            "actor-colony" => memo with { Actor = memo.Actor with { ColonyId = "other-colony" } },
            "target" => memo with { Target = memo.Target with { UserId = "other" } },
            "target-colony" => memo with { Target = memo.Target with { ColonyId = "other-colony" } },
            "faction" => memo with { Actor = memo.Actor with { FactionId = "wrong" } },
            "type" => memo with { Type = ServerEventType.ItemDelivery },
            "stage" => memo with { Payload = payload with { Stage = TradeStage.MarketOrder } },
            "order" => memo with { Payload = payload with { TradeId = "another-order" } },
            "accepted-user" => memo with { Payload = payload with { AcceptedByUserId = "other" } },
            "fee" => memo with { Payload = payload with { FeeSilver = 99 } },
            "offered" => memo with { Payload = payload with { OfferedItems = [payload.OfferedItems[0] with { StackCount = 99 }] } },
            "requested" => memo with { Payload = payload with { RequestedItems = [payload.RequestedItems[0] with { Def = "Gold" }] } },
            "postage" => memo with { Payload = payload with { PostagePaidByAcceptor = true } },
            "exchange" => memo with { Payload = payload with { FulfillmentMode = TradeFulfillmentMode.ServerDropPod } },
            "memo-link" => memo with { Payload = payload with { AcceptedMemoEventId = "another-memo" } },
            "context" => memo with { TargetContext = new EventTargetContext("world", "map", 1, EventLandingMode.CenterNear) },
            _ => memo
        };
        ledger.Append(memo);
        if (corruption == "multiple") ledger.Append(Memo(order, "duplicate-key"));
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(state);
        Conflict(Accept(state, order, "key", token), "corrupt memo: " + corruption);
        // A fresh key must not bypass a related malformed active memo either.
        if (corruption is not ("stage" or "order")) Conflict(Accept(state, order, "fresh-key", token), "fresh key masked corruption: " + corruption);
        Require(before == Snapshot(state), "corrupt memo handling changed events or notifications");
    });

    private static void ClosedMemo(string storage, ServerEventStatus status) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var order = Order("order"); ledger.Append(order);
        var memo = Memo(order, "key") with { Status = status }; ledger.Append(memo);
        var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(state);
        Conflict(Accept(state, order, "key", token), "closed key was reused: " + status);
        Require(before == Snapshot(state), "closed memo was rewritten");
        var fresh = Accept(state, order, "new-key", token);
        Require(fresh.Result.Accepted && fresh.MemoCreated && fresh.MemoEventId != memo.EventId
            && state.Ledger.Find(memo.EventId)!.Status == status, "new acceptance reset closed memo");
    });

    private static void Concurrent(string storage) => WithRoot(root =>
    {
        var state = State(Open(storage, root)); var order = Order("order"); state.Ledger.Append(order);
        string token = TradeCommandAuthTests.Token(state, "buyer"); var results = new ConcurrentBag<AcceptTradeOrderResponse>();
        Parallel.For(0, 12, i => results.Add(Accept(state, order, "key-" + i % 3, token)));
        Require(results.All(r => r.Result.Accepted) && results.Count(r => r.MemoCreated) == 1
            && results.Select(r => r.MemoEventId).Distinct().Count() == 1 && state.Ledger.ListAll().Count == 2,
            "concurrent acceptance created different memos");
    });

    private static void SqlFailure() => WithRoot(root =>
    {
        var state = State(Open("sqlite", root)); var order = Order("order"); state.Ledger.Append(order);
        string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(state);
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "create trigger fail_accept before insert on events begin select raise(abort, 'injected acceptance'); end;";
        command.ExecuteNonQuery(); bool failed = false;
        try { Accept(state, order, "key", token); } catch (SqliteException) { failed = true; }
        Require(failed && before == Snapshot(state) && Open("sqlite", root).ListAll().Count == 1, "failed acceptance leaked memo");
        command.CommandText = "drop trigger fail_accept;"; command.ExecuteNonQuery();
        Require(Accept(state, order, "key", token).MemoCreated, "failed acceptance could not retry");
    });

    private static AuthoritativeEvent Order(string key) => AuthoritativeEventFactory.Create(ServerEventType.Trade,
        new("seller", "seller-colony"), new("server"), key, false, new TradeEventPayload(key, TradeStage.MarketOrder,
            [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow);
    private static AuthoritativeEvent Memo(AuthoritativeEvent order, string key)
    {
        var payload = (TradeEventPayload)order.Payload;
        return AuthoritativeEventFactory.Create(ServerEventType.Trade, new("buyer", "buyer-colony"), new("buyer", "buyer-colony"),
            key, false, new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, payload.OfferedItems, payload.RequestedItems, payload.FeeSilver,
                AcceptedByUserId: "buyer"), DateTimeOffset.UtcNow, order.TargetContext) with { Status = ServerEventStatus.Recorded };
    }
    private static AcceptTradeOrderResponse Accept(ClashOfRimNetworkState state, AuthoritativeEvent order, string key, string token,
        string user = "buyer", bool postage = false, string snapshot = "base")
    {
        return state.Trades.AcceptTradeOrder(new AcceptTradeOrderRequest(key, order.EventId, new(user, user + "-colony", snapshot), postage, token));
    }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger,
        serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    {
        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        _ => new InMemoryAuthoritativeEventLedger()
    };
    private static string Snapshot(ClashOfRimNetworkState state) => JsonSerializer.Serialize(state.Ledger.ListAll())
        + state.EventNotifications.GetVersion("buyer") + "/" + state.EventNotifications.GetVersion("seller");
    private static void Conflict(AcceptTradeOrderResponse response, string message) =>
        Require(!response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.Conflict, message);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-acceptance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
