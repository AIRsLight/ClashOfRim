using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class AdminBaselineRecoveryTests
{
    private static readonly string[] Stores = ["memory", "file", "sqlite"];
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization(); int count = 0;
        void Case(Action action) { action(); count++; }
        foreach (string storage in Stores)
        {
            foreach (string stage in new[] { "baseline-before", "baseline-after", "ledger-before", "ledger-after", "second-before", "second-after", "complete-before", "complete-after" })
                Case(() => Interrupted(storage, stage));
            Case(() => FixedPlan(storage));
            Case(() => Consumed(storage, existing: true));
            Case(() => Consumed(storage, existing: false));
            Case(() => PendingCannotBeOverwritten(storage));
            Case(() => ConflictingLedger(storage));
            foreach (string corruption in new[] { "null", "version", "duplicate", "batch", "order", "json", "refund", "notification", "refund-items", "notification-target" }) Case(() => Corruption(storage, corruption));
        }
        foreach (string failure in new[] { "baseline", "ledger", "completion" }) Case(() => SqlFailure(failure));
        Case(Startup); Case(Migration); Case(Concurrent);
        foreach (string storage in new[] { "file", "sqlite" })
        {
            Case(() => HttpRetry(storage));
            foreach (string stage in new[] { "baseline-after", "ledger-before", "second-after", "complete-before" }) Case(() => ProcessCrash(storage, stage));
        }
        Console.WriteLine($"Baseline recovery scenarios passed: {count}");
    }

    private static void Interrupted(string storage, string stage)
    {
        using var f = new Fixture(storage); var state = f.Seed();
        var before = state.AdminBaseline.Current; var policy = state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration);
        string durable = f.Slot.Read()!, ledgerBefore = Snapshot(state);
        f.Fault.Stage = stage;
        Expect(() => Submit(state));
        bool committed = stage != "baseline-before";
        if (!committed)
            Require(ReferenceEquals(before, state.AdminBaseline.Current) && f.Slot.Read() == durable && Snapshot(state) == ledgerBefore
                && ReferenceEquals(policy, state.AdminBaseline.BuildEffectiveTradeFeePolicy(state.ServerConfiguration)), "Failed baseline published prices, pending work or refunds.");
        f.Fault.Clear(); state = f.Open();
        ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
        if (committed) AssertRecovered(state);
        else Require(Snapshot(state) == ledgerBefore && state.AdminBaseline.Current!.StandardMarketValuePerThing.ContainsKey("Steel"), "Restart applied an uncommitted baseline.");
        Submit(state); AssertRecovered(state);
        string completed = Snapshot(state); long signals = state.EventNotifications.GetVersion("seller");
        ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
        Require(Snapshot(state) == completed && signals == state.EventNotifications.GetVersion("seller"), "Completed baseline work was replayed again.");
        AssertRecovered(f.Open());
    }

    private static void FixedPlan(string storage)
    {
        using var f = new Fixture(storage); var state = f.Seed(); f.Fault.Stage = "ledger-before";
        Expect(() => Submit(state)); f.Fault.Clear();
        // A future order and a new baseline must not alter the already persisted decision.
        var future = Order("future"); state.Ledger.Append(future);
        state = f.Open(); Submit(state, allowSteel: true);
        AssertRecovered(state);
        Require(state.Ledger.Find(future.EventId)!.Status != ServerEventStatus.Cancelled && state.AdminBaseline.Current!.StandardMarketValuePerThing.ContainsKey("Steel"),
            "Recovery recomputed the fixed plan or overwrote the later baseline.");
    }

    private static void Consumed(string storage, bool existing)
    {
        using var f = new Fixture(storage); var state = f.Seed();
        if (existing)
        {
            foreach (var order in state.Ledger.ListByType(ServerEventType.Trade).Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder }))
            {
                var refund = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
                    "trade-baseline-invalidated-owner-return:" + order.EventId, false,
                    new ItemDeliveryEventPayload(((TradeEventPayload)order.Payload).OfferedItems, null, Purpose: ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn), DateTimeOffset.UnixEpoch, order.TargetContext);
                state.Ledger.Append(refund); Consume(state.Ledger, refund);
                foreach (var target in new[] { order.Actor, new EventParty("buyer", "buyer-colony") })
                {
                    string id = "trade-baseline-invalidated:" + order.EventId + ":" + target.UserId + ":" + target.ColonyId;
                    var notification = AuthoritativeEventFactory.Create(ServerEventType.ServerNotification, new("server"), target, id, false,
                        new ServerNotificationEventPayload(id, "old title", "old localized text", ServerNotificationSeverity.Warning, false), DateTimeOffset.UnixEpoch);
                    state.Ledger.Append(notification); Consume(state.Ledger, notification);
                }
            }
        }
        f.Fault.Stage = "ledger-after"; Expect(() => Submit(state)); f.Fault.Clear();
        if (!existing) foreach (var item in state.Ledger.ListAll().Where(item => item.Type is ServerEventType.ItemDelivery or ServerEventType.ServerNotification)) Consume(state.Ledger, item);
        var protectedIds = state.Ledger.ListAll().Where(item => item.Type is ServerEventType.ItemDelivery or ServerEventType.ServerNotification).Select(item => item.EventId).ToArray();
        string before = JsonSerializer.Serialize(protectedIds.Select(state.Ledger.Find));
        state = f.Open(); ClashOfRimNetworkServer.RecoverSnapshotCommits(state); AssertRecovered(state);
        Require(before == JsonSerializer.Serialize(protectedIds.Select(state.Ledger.Find)), "Recovery rewrote consumed refunds or notifications.");
    }

    private static void PendingCannotBeOverwritten(string storage)
    {
        using var f = new Fixture(storage); var state = f.Seed(); f.Fault.Stage = "ledger-before";
        Expect(() => Submit(state)); f.Fault.Clear(); string durable = f.Slot.Read()!;
        Expect(() => state.AdminBaseline.Submit(Request(allowSteel: true)), typeof(InvalidOperationException));
        Require(f.Slot.Read() == durable, "Direct baseline replacement dropped pending work.");
        f.Fault.Stage = "ledger-before";
        var rejected = Call(state, Request("invalid"));
        Require(!rejected.Result.Accepted && f.Fault.Batches == 0 && f.Slot.Read() == durable, "Unauthorized submission recovered pending work.");
        Expect(() => Submit(state, allowSteel: true));
        Require(f.Slot.Read() == durable, "Failed recovery replaced the pending baseline.");
        f.Fault.Clear(); Submit(state, allowSteel: true); AssertRecovered(state);
    }

    private static void Corruption(string storage, string kind)
    {
        using var f = new Fixture(storage); var state = f.Seed(); f.Fault.Stage = "ledger-before"; Expect(() => Submit(state)); f.Fault.Clear();
        var json = JsonNode.Parse(f.Slot.Read()!)!.AsObject(); var pending = json["PendingTradeInvalidations"]!.AsArray();
        switch (kind)
        {
            case "null": json["PendingTradeInvalidations"] = null; break;
            case "version": pending[0]!["Version"] = 99; break;
            case "duplicate": pending.Add(pending[0]!.DeepClone()); break;
            case "batch": pending[0]!["Batch"] = null; break;
            case "order": pending[0]!["OrderId"] = "unrelated"; break;
            case "refund": case "notification": case "refund-items": case "notification-target":
                var events = pending[0]!["Batch"]!["Events"]!.AsArray();
                var item = events.First(item => item!["Type"]!.GetValue<string>() == (kind.StartsWith("refund", StringComparison.Ordinal) ? "ItemDelivery" : "ServerNotification"))!;
                if (kind is "refund" or "notification") events.Remove(item);
                else if (kind == "refund-items") item["Payload"]!["Items"] = new JsonArray();
                else item["Target"]!["UserId"] = "unrelated";
                break;
        }
        f.Slot.Write(kind == "json" ? "{" : json.ToJsonString()); string evidence = f.Slot.Read()!;
        Expect(() => f.Open(), typeof(InvalidOperationException)); Require(f.Slot.Read() == evidence, "Corrupt intent was rewritten on load.");
    }

    private static void ConflictingLedger(string storage)
    {
        using var f = new Fixture(storage); var state = f.Seed(); f.Fault.Stage = "ledger-before"; Expect(() => Submit(state)); f.Fault.Clear();
        string durable = f.Slot.Read()!;
        state.Ledger.ChangeStatus(state.AdminBaseline.ListPendingTradeInvalidations()[0].OrderId, ServerEventStatus.AppliedToSnapshot);
        string evidence = Snapshot(state); state = f.Open();
        Expect(() => ClashOfRimNetworkServer.RecoverSnapshotCommits(state), typeof(InvalidOperationException));
        Require(f.Slot.Read() == durable && Snapshot(state) == evidence, "Recovery guessed past changed order evidence or dropped the pending decision.");
    }

    private static void HttpRetry(string storage)
    {
        using var f = new Fixture(storage); var state = f.Seed();
        Directory.CreateDirectory(Path.Combine(f.Root, "Localization"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json"), Path.Combine(f.Root, "Localization", "English.json"));
        var app = ClashOfRimNetworkServer.Build(["--contentRoot", f.Root, "--urls", "http://127.0.0.1:0", "--language=English", "--Logging:LogLevel:Default", "Critical"], state);
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            string token = Authenticate(state); string route = ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route;
            f.Fault.Stage = "ledger-before";
            using var failed = client.PostAsJsonAsync(route, Request(token)).GetAwaiter().GetResult();
            Require(failed.StatusCode == HttpStatusCode.InternalServerError && state.AdminBaseline.ListPendingTradeInvalidations().Count == 2,
                "HTTP failure lost its committed baseline intent.");
            f.Fault.Clear(); f.Fault.Stage = "ledger-before"; string durable = f.Slot.Read()!;
            using var denied = client.PostAsJsonAsync(route, Request("invalid")).GetAwaiter().GetResult();
            using var deniedBody = JsonDocument.Parse(denied.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Require(denied.IsSuccessStatusCode && !deniedBody.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean()
                && f.Fault.Batches == 0 && f.Slot.Read() == durable, "Rejected HTTP request recovered baseline work.");
            f.Fault.Clear();
            using var retry = client.PostAsJsonAsync(route, Request(token, allowSteel: true)).GetAwaiter().GetResult();
            using var body = JsonDocument.Parse(retry.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Require(retry.IsSuccessStatusCode && body.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean(), "HTTP retry failed.");
            AssertRecovered(state); AssertRecovered(f.Open());
        }
        finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void ProcessCrash(string storage, string stage)
    {
        using var f = new Fixture(storage); f.Seed();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(AdminBaselineRecoveryTests).Assembly.Location);
        foreach (string arg in new[] { "--baseline-invalidation-crash", f.Root, storage, stage }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); throw new Exception("Baseline crash child timed out."); }
        Require(child.ExitCode == 75, "Child missed baseline crash checkpoint: " + output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        var state = f.Open(); ClashOfRimNetworkServer.RecoverSnapshotCommits(state); AssertRecovered(state);
    }

    internal static void CrashChild(string[] args)
    {
        SnapshotEndpointTestClient.LoadLocalization(); using var f = new Fixture(args[2], args[1]);
        f.Fault.Stage = args[3]; f.Fault.Stop = () => Environment.Exit(75); Submit(f.Open());
        throw new Exception("Child did not reach baseline crash checkpoint.");
    }

    private static void SqlFailure(string phase)
    {
        using var f = new Fixture("sqlite"); var state = f.Seed(); string before = Snapshot(state);
        string database = phase == "ledger" ? f.EventsDatabase : f.Database;
        string sql = phase == "ledger" ? "create trigger fail before insert on events when new.type='ServerNotification' begin select raise(abort, 'fault'); end;"
            : "create trigger fail before update on server_documents when new.document_key='admin-baseline'" +
                (phase == "completion" ? " and json_array_length(new.content_json, '$.PendingTradeInvalidations') < json_array_length(old.content_json, '$.PendingTradeInvalidations')" : "") + " begin select raise(abort, 'fault'); end;";
        Sql(database, sql); Expect(() => Submit(state), typeof(SqliteException));
        Require(phase == "completion" || Snapshot(state) == before, "SQLite failure partially published an invalidation batch.");
        Sql(database, "drop trigger fail;"); state = f.Open();
        if (phase == "baseline") Submit(state); else ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
        AssertRecovered(state);
    }

    private static void Startup()
    {
        using var f = new Fixture("sqlite"); var state = f.Seed(); f.Fault.Stage = "ledger-before"; Expect(() => Submit(state)); f.Fault.Clear();
        state = f.Open(); Directory.CreateDirectory(Path.Combine(f.Root, "Localization"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json"), Path.Combine(f.Root, "Localization", "English.json"));
        var app = ClashOfRimNetworkServer.Build(["--contentRoot", f.Root, "--urls", "http://127.0.0.1:0", "--language=English", "--Logging:LogLevel:Default", "Warning"], state);
        try { AssertRecovered(state); } finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void Migration()
    {
        using var f = new Fixture("sqlite"); var state = f.Seed();
        var legacy = JsonNode.Parse(f.Slot.Read()!)!.AsObject(); legacy.Remove("PendingTradeInvalidations"); f.Slot.Write(legacy.ToJsonString());
        string before = JsonSerializer.Serialize(f.Open().AdminBaseline.Current);
        Sql(f.Database, "update server_schema_metadata set metadata_value='28' where metadata_key='schema_version';");
        var migration = new ServerPersistenceMigrationService(f.Root).PrepareForStartup().Migration;
        Require(migration is not null && migration.Database.AppliedMigrations.Contains("28->29") && Directory.Exists(migration.BackupDirectory)
            && JsonSerializer.Serialize(f.Open().AdminBaseline.Current) == before, "Baseline migration lost legacy data or backup.");
        Submit(f.Open()); AssertRecovered(f.Open());
    }
    private static void Concurrent()
    {
        using var f = new Fixture("sqlite"); var state = f.Seed(); f.Fault.Stage = "ledger-before"; Expect(() => Submit(state)); f.Fault.Clear();
        Task.WaitAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => ClashOfRimNetworkServer.RecoverSnapshotCommits(state))).ToArray());
        AssertRecovered(state); Require(state.EventNotifications.GetVersion("seller") == 2, "Concurrent recovery duplicated wakeups.");
    }

    private static void AssertRecovered(ClashOfRimNetworkState state)
    {
        Require(state.Ledger.ListByType(ServerEventType.Trade).Where(item => ((TradeEventPayload)item.Payload).Stage == TradeStage.MarketOrder && item.IdempotencyKey != "future")
                .All(item => item.Status == ServerEventStatus.Cancelled)
            && state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 2 && state.Ledger.ListByType(ServerEventType.ServerNotification).Count == 4
            && state.AdminBaseline.ListPendingTradeInvalidations().Count == 0, "Restart lost, duplicated or retained baseline invalidations.");
    }
    private static void Consume(IAuthoritativeEventLedger ledger, AuthoritativeEvent item) { ledger.MarkDelivered(item.EventId, "before", DateTimeOffset.UnixEpoch); ledger.MarkApplied(item.EventId, "after", DateTimeOffset.UnixEpoch); }
    private static string Snapshot(ClashOfRimNetworkState state) => JsonSerializer.Serialize(state.Ledger.ListAll());
    private static AuthoritativeEvent Order(string key) => AuthoritativeEventFactory.Create(ServerEventType.Trade,
        new("seller", "seller-colony"), new("server"), key, false,
        new TradeEventPayload(key, TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UnixEpoch);
    private static SubmitAdminBaselineRequest Request(string? token = null, bool allowSteel = false) => new(ProtocolApiVersion.Current, "admin", "colony",
        DateTimeOffset.UnixEpoch, allowSteel ? [new("Steel", 7), new("Silver", 1)] : [new("Silver", 1)], [], authToken: token);
    private static void Submit(ClashOfRimNetworkState state, bool allowSteel = false)
        => Require(Call(state, Request(Authenticate(state), allowSteel)).Result.Accepted, "Administrator baseline was rejected.");

    private static string Authenticate(ClashOfRimNetworkState state)
    {
        state.WorldConfiguration.Prepare("admin"); var now = DateTimeOffset.UtcNow; state.LoginSessions.EndUser("admin");
        state.LoginSessions.TryCreate("admin", "colony", now, out var session);
        return state.AuthTokens.Issue("admin", "admin", "colony", session, now);
    }
    private static SubmitAdminBaselineResponse Call(ClashOfRimNetworkState state, SubmitAdminBaselineRequest request)
    {
        try { return (SubmitAdminBaselineResponse)((IValueHttpResult)typeof(ClashOfRimNetworkServer).GetMethod("SubmitAdminBaseline", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [request, state])!).Value!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Expect(Action action, params Type[] types)
    { try { action(); } catch (Exception ex) when (types.Length == 0 ? ex is IOException : types.Any(type => type.IsInstanceOfType(ex))) { return; } throw new Exception("Expected interruption missing."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Sql(string database, string sql) { using var c = new SqliteConnection("Data Source=" + database); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
    private sealed class MemorySlot : IJsonPersistenceSlot { private string? value; public string? Read() => value; public void Write(string json) => value = json; }
    private sealed class JsonFault(IJsonPersistenceSlot inner, Fault fault) : IJsonPersistenceSlot
    {
        public string? Read() => inner.Read();
        public void Write(string json)
        {
            int n = ++fault.Writes;
            fault.Hit(n == 1 ? "baseline-before" : "complete-before"); inner.Write(json);
            fault.Hit(n == 1 ? "baseline-after" : "complete-after");
        }
    }
    public sealed class Fault
    {
        public string Stage = ""; public int Writes, Batches; public Action? Stop;
        public void Clear() { Stage = ""; Writes = Batches = 0; }
        public void Hit(string stage) { if (Stage == stage) { Stop?.Invoke(); throw new IOException("Interrupted at " + stage); } }
    }
    public class LedgerFault : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!; public Fault Fault = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            bool batch = method!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch); int n = batch ? ++Fault.Batches : 0;
            if (batch) Fault.Hit(n == 1 ? "ledger-before" : "second-before");
            object? result;
            try { result = method.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (batch) Fault.Hit(n == 1 ? "ledger-after" : "second-after"); return result;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root;
        private readonly bool ownsRoot;
        public string Database => Path.Combine(Root, "server.sqlite"); public string EventsDatabase => Path.Combine(Root, "events.sqlite");
        public readonly IJsonPersistenceSlot Slot; public readonly Fault Fault = new(); private readonly string storage; private readonly InMemoryAuthoritativeEventLedger memory = new();
        public Fixture(string storage, string? root = null)
        {
            ownsRoot = root is null;
            Root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "cor-baseline-invalidation-" + Guid.NewGuid().ToString("N")));
            Require(Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(Root).StartsWith("cor-baseline-invalidation-", StringComparison.Ordinal), "Unsafe test root.");
            this.storage = storage; Directory.CreateDirectory(Root);
            if (storage == "sqlite") ServerDatabaseMigrator.Migrate(Database);
            Slot = storage switch { "file" => new FileJsonPersistenceSlot(Path.Combine(Root, "baseline.json")), "sqlite" => new SqliteJsonPersistenceSlot(Database, "admin-baseline"), _ => new MemorySlot() };
        }
        public ClashOfRimNetworkState Open()
        {
            IAuthoritativeEventLedger raw = storage switch { "file" => new FileAuthoritativeEventLedger(Path.Combine(Root, "events.json")), "sqlite" => new SqliteAuthoritativeEventLedger(EventsDatabase), _ => memory };
            var ledger = DispatchProxy.Create<IAuthoritativeEventLedger, LedgerFault>(); var proxy = (LedgerFault)ledger; proxy.Inner = raw; proxy.Fault = Fault;
            return new(ledger: ledger, adminBaselineRegistry: new AdminBaselineRegistry(new JsonFault(Slot, Fault)));
        }
        public ClashOfRimNetworkState Seed()
        {
            var state = Open(); state.AdminBaseline.Submit(Request(allowSteel: true));
            foreach (string key in new[] { "first", "second" })
            {
                var order = Order(key); state.Ledger.Append(order); var p = (TradeEventPayload)order.Payload;
                state.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, new("buyer", "buyer-colony"), new("buyer", "buyer-colony"), key + "-memo", false,
                    new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, 0, AcceptedByUserId: "buyer"), DateTimeOffset.UnixEpoch) with { Status = ServerEventStatus.Recorded });
            }
            Fault.Clear(); return state;
        }
        public void Dispose()
        {
            Require(Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(Root).StartsWith("cor-baseline-invalidation-", StringComparison.Ordinal), "Unsafe cleanup root.");
            if (ownsRoot) { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
        }
    }
}
