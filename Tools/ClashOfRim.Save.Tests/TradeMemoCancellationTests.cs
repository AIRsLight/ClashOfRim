using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class TradeMemoCancellationTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            OldRetryCannotCancelNewMemo(storage);
            foreach (bool reopen in new[] { false, true }) Interrupted(storage, reopen);
            foreach (string corruption in new[] { "actor", "target", "order", "memo", "fee", "postage", "status", "type", "stage", "context", "missing-memo", "source-owner", "source-items" })
                Corruption(storage, corruption);
            Concurrent(storage);
        }
        foreach (string phase in new[] { "insert", "update" }) SqlFailure(phase);
        if (OperatingSystem.IsWindows()) FileFailure();
        Migration();
    }

    private static void OldRetryCannotCancelNewMemo(string storage) => WithRoot(root =>
    {
        var state = State(Open(storage, root)); var (order, memo) = Seed(state.Ledger);
        string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(state.Ledger);
        Require(!Cancel(state, order, token, memo.IdempotencyKey).Result.Accepted && before == Snapshot(state.Ledger),
            "cancellation accepted a key bound to an acceptance");
        Require(Cancel(state, order, token).Result.Accepted, "first cancellation failed");
        var next = Memo(order, "new-acceptance"); state.Ledger.Append(next); before = Snapshot(state.Ledger);
        var retry = Cancel(state, order, token);
        Require(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && before == Snapshot(state.Ledger) && state.Ledger.Find(next.EventId)!.Status == ServerEventStatus.Recorded,
            "old cancellation request cancelled new acceptance");
        // Historical cancellations have no request receipt: do not invent one from a cancelled memo.
        var legacy = State(new InMemoryAuthoritativeEventLedger()); var (oldOrder, oldMemo) = Seed(legacy.Ledger);
        legacy.Ledger.ChangeStatus(oldMemo.EventId, ServerEventStatus.Cancelled); before = Snapshot(legacy.Ledger);
        Require(!Cancel(legacy, oldOrder, TradeCommandAuthTests.Token(legacy, "buyer")).Result.Accepted
            && before == Snapshot(legacy.Ledger), "legacy cancellation inferred an unknown receipt");
    });

    private static void Interrupted(string storage, bool reopen) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var (order, memo) = Seed(ledger);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedLedger>();
        var fault = (InterruptedLedger)proxy; fault.Inner = ledger; fault.Fail = true;
        var state = State(proxy); string token = TradeCommandAuthTests.Token(state, "buyer");
        bool failed = false;
        try { Cancel(state, order, token); } catch (IOException) { failed = true; }
        Require(failed && ledger.Find(memo.EventId)!.Status == ServerEventStatus.Cancelled
            && ledger.FindByIdempotencyKey("cancel-key") is not null && state.EventNotifications.GetVersion("seller") == 0,
            "cancellation did not commit before interrupted notification");
        fault.Fail = false;
        if (reopen) state = State(storage == "memory" ? ledger : Open(storage, root));
        token = TradeCommandAuthTests.Token(state, "buyer");
        var receipt = state.Ledger.FindByIdempotencyKey("cancel-key")!;
        Require(!EventSemanticClassifier.RequiresClientApplication(receipt)
            && !EventQueueSummaryBuilder.BuildForTarget("seller", state.Ledger.ListAll()).AllItems.Any(x => x.EventId == receipt.EventId),
            "cancellation receipt entered player application queue");
        var nextMemo = Memo(order, "next-acceptance"); state.Ledger.Append(nextMemo);
        string before = Snapshot(state.Ledger);
        var retry = Cancel(state, order, token);
        Require(retry.Result.Accepted && retry.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && retry.NotifiedAcceptorCount == 2
            && before == Snapshot(state.Ledger) && state.EventNotifications.GetVersion("seller") == 1
            && state.EventNotifications.GetVersion("buyer") == 1, "retry failed to wake original parties or changed the newer memo");
        Require(Cancel(state, order, token, "new-cancel").Result.Accepted
            && state.Ledger.Find(nextMemo.EventId)!.Status == ServerEventStatus.Cancelled, "fresh cancellation failed to select newer memo");
        state.Ledger.ChangeStatus(order.EventId, ServerEventStatus.Cancelled); before = Snapshot(state.Ledger);
        Require(Cancel(state, order, token).Result.Accepted && before == Snapshot(state.Ledger), "order closure prevented receipt replay");
        var otherOrder = Order("another-order"); state.Ledger.Append(otherOrder); before = Snapshot(state.Ledger);
        Require(!Cancel(state, otherOrder, token).Result.Accepted && before == Snapshot(state.Ledger), "receipt reused on another order");
    });

    private static void Corruption(string storage, string kind) => WithRoot(root =>
    {
        var original = State(new InMemoryAuthoritativeEventLedger()); var (order, memo) = Seed(original.Ledger);
        Require(Cancel(original, order, TradeCommandAuthTests.Token(original, "buyer")).Result.Accepted, "receipt setup failed");
        var receipt = original.Ledger.FindByIdempotencyKey("cancel-key")!; var payload = (TradeEventPayload)receipt.Payload;
        receipt = kind switch
        {
            "actor" => receipt with { Actor = new("other", "other-colony") },
            "target" => receipt with { Target = new("other", "other-colony") },
            "order" => receipt with { Payload = payload with { TradeId = "other-order" } },
            "memo" => receipt with { Payload = payload with { AcceptedMemoEventId = "other-memo" } },
            "fee" => receipt with { Payload = payload with { FeeSilver = 99 } },
            "postage" => receipt with { Payload = payload with { PostagePaidByAcceptor = true } },
            "status" => receipt with { Status = ServerEventStatus.Recorded },
            "type" => receipt with { Type = ServerEventType.ItemDelivery },
            "stage" => receipt with { Payload = payload with { Stage = TradeStage.Completed } },
            "context" => receipt with { TargetContext = new("world", "map", 1, EventLandingMode.CenterNear) },
            _ => receipt
        };
        if (kind == "source-owner") order = order with { Actor = new("other", "other-colony") };
        if (kind == "source-items") order = order with { Payload = ((TradeEventPayload)order.Payload) with { OfferedItems = [] } };
        var ledger = Open(storage, root); ledger.Append(order); ledger.Append(receipt);
        if (kind != "missing-memo") ledger.Append(memo with { Status = ServerEventStatus.Cancelled });
        if (storage != "memory") ledger = Open(storage, root);
        var state = State(ledger); string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(ledger);
        Require(!Cancel(state, order, token).Result.Accepted && before == Snapshot(ledger)
            && state.EventNotifications.GetVersion("buyer") == 0 && state.EventNotifications.GetVersion("seller") == 0,
            "corrupt cancellation receipt accepted: " + kind);
    });

    private static void Concurrent(string storage) => WithRoot(root =>
    {
        var state = State(Open(storage, root)); var (order, _) = Seed(state.Ledger); string token = TradeCommandAuthTests.Token(state, "buyer");
        var results = new System.Collections.Concurrent.ConcurrentBag<CloseTradeOrderResponse>();
        Parallel.For(0, 12, _ => results.Add(Cancel(state, order, token)));
        Require(results.All(r => r.Result.Accepted) && results.Count(r => r.Result.ErrorCode == ProtocolErrorCode.None) == 1
            && state.Ledger.ListAll().Count == 3 && state.EventNotifications.GetVersion("seller") == 12, "concurrent cancellation lost receipt or wake");
    });

    private static void SqlFailure(string phase) => WithRoot(root =>
    {
        var ledger = Open("sqlite", root); var (order, _) = Seed(ledger); var state = State(ledger);
        string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(ledger);
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"create trigger fail_cancel before {phase} on events begin select raise(abort, 'injected cancellation'); end;";
        command.ExecuteNonQuery(); bool failed = false;
        try { Cancel(state, order, token); } catch (SqliteException) { failed = true; }
        Require(failed && before == Snapshot(ledger) && before == Snapshot(Open("sqlite", root)), "receipt and cancellation partially committed");
        command.CommandText = "drop trigger fail_cancel;"; command.ExecuteNonQuery();
        state = State(Open("sqlite", root)); token = TradeCommandAuthTests.Token(state, "buyer");
        Require(Cancel(state, order, token).Result.Accepted, "cancellation retry after rollback failed");
    });

    private static void FileFailure() => WithRoot(root =>
    {
        var ledger = Open("file", root); var (order, _) = Seed(ledger); var state = State(ledger);
        string token = TradeCommandAuthTests.Token(state, "buyer"); string before = Snapshot(ledger); bool failed = false;
        using (var blocked = new FileStream(Path.Combine(root, "events.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Cancel(state, order, token); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed && before == Snapshot(ledger), "failed file cancellation published cache");
        }
        Require(before == Snapshot(Open("file", root)) && Cancel(state, order, token).Result.Accepted, "file cancellation could not retry");
    });

    private static void Migration() => WithRoot(root =>
    {
        string path = Path.Combine(root, "events.sqlite"); ServerDatabaseMigrator.Migrate(path);
        var ledger = Open("sqlite", root); Seed(ledger); string before = Snapshot(ledger);
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "update server_schema_metadata set metadata_value='25' where metadata_key='schema_version'"; command.ExecuteNonQuery();
        }
        Require(ServerDatabaseMigrator.Assess(path).Status == ServerPersistenceMigrationStatus.SafeMigrationAvailable, "version 25 not migratable");
        ServerDatabaseMigrator.Migrate(path);
        Require(ServerDatabaseMigrator.ReadVersion(path) == ServerDatabaseSchema.CurrentVersion && before == Snapshot(Open("sqlite", root)), "version gate rewrote legacy events");
    });

    private static AuthoritativeEvent Order(string key) => AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"),
        new("server"), key, false, new TradeEventPayload(key, TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow);
    private static AuthoritativeEvent Memo(AuthoritativeEvent order, string key)
    {
        var p = (TradeEventPayload)order.Payload;
        return AuthoritativeEventFactory.Create(ServerEventType.Trade, new("buyer", "buyer-colony"), new("buyer", "buyer-colony"), key, false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, p.FeeSilver, AcceptedByUserId: "buyer"),
            DateTimeOffset.UtcNow) with { Status = ServerEventStatus.Recorded };
    }
    private static (AuthoritativeEvent Order, AuthoritativeEvent Memo) Seed(IAuthoritativeEventLedger ledger)
    { var order = Order("order"); var memo = Memo(order, "memo"); ledger.Append(order); ledger.Append(memo); return (order, memo); }
    private static CloseTradeOrderResponse Cancel(ClashOfRimNetworkState state, AuthoritativeEvent order, string token, string key = "cancel-key")
    {
        return state.Trades.CancelTradeOrder(new CloseTradeOrderRequest(key, order.EventId, new("buyer", "buyer-colony", "base"), "cancel", token));
    }
    private static ClashOfRimNetworkState State(IAuthoritativeEventLedger ledger) => new(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    { "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")), "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")), _ => new InMemoryAuthoritativeEventLedger() };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "cor-memo-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
    public class InterruptedLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!; public bool Fail;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (method!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch) && Fail) throw new IOException("after cancellation commit");
            return result;
        }
    }
}
