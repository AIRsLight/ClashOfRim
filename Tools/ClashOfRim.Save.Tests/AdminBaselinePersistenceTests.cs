using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class AdminBaselinePersistenceTests
{
    private static readonly ClashOfRimServerConfiguration Configuration = new(tradeFeePolicy: new TradeFeePolicy(
        baseFeeRate: 0.1f, standardMarketValuePerThing: new Dictionary<string, float> { ["Steel"] = 3 }));

    public static void Run()
    {
        foreach (bool existing in new[] { false, true })
        foreach (bool warmCache in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        {
            var slot = new FailingSlot(); var registry = new AdminBaselineRegistry(slot);
            if (existing) registry.Submit(Request(10));
            var before = registry.Current; var policy = warmCache ? registry.BuildEffectiveTradeFeePolicy(Configuration) : null;
            string? stored = slot.Read(); slot.Fail = true;
            ExpectFailure(() => registry.Submit(Request(90)), typeof(IOException));
            Unchanged(registry, before, policy, existing);
            Require(slot.Read() == stored && Snapshot(new AdminBaselineRegistry(slot).Current) == Snapshot(before), "Failed JSON baseline changed storage.");
            slot.Fail = false; if (reopen) registry = new(slot);
            Retry(registry, () => new(slot));
        }
        foreach (bool existing in new[] { false, true })
        foreach (string phase in existing ? new[] { "insert", "update" } : new[] { "insert" })
        foreach (bool reopen in new[] { false, true }) WithRoot(root =>
        {
            string database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database);
            AdminBaselineRegistry Open() => new(new SqliteJsonPersistenceSlot(database, "admin-baseline"));
            var registry = Open(); if (existing) registry.Submit(Request(10));
            var before = registry.Current; var policy = registry.BuildEffectiveTradeFeePolicy(Configuration);
            Sql(database, $"create trigger fail_baseline before {phase} on server_documents when new.document_key = 'admin-baseline' begin select raise(abort, 'baseline write'); end;");
            ExpectFailure(() => registry.Submit(Request(90)), typeof(SqliteException));
            Unchanged(registry, before, policy, existing);
            Require(Snapshot(Open().Current) == Snapshot(before), "Failed SQLite baseline changed storage.");
            Sql(database, "drop trigger fail_baseline;"); if (reopen) registry = Open();
            Retry(registry, Open);
        });
        if (OperatingSystem.IsWindows()) foreach (bool reopen in new[] { false, true }) WithRoot(root =>
        {
            string path = Path.Combine(root, "baseline.json"); AdminBaselineRegistry Open() => new(path);
            var registry = Open(); registry.Submit(Request(10)); var before = registry.Current; var policy = registry.BuildEffectiveTradeFeePolicy(Configuration);
            byte[] bytes = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectFailure(() => registry.Submit(Request(90)), typeof(IOException), typeof(UnauthorizedAccessException));
                Unchanged(registry, before, policy, existing: true);
                Require(bytes.SequenceEqual(File.ReadAllBytes(path)) && Snapshot(Open().Current) == Snapshot(before), "Failed file baseline changed storage.");
            }
            if (reopen) registry = Open(); Retry(registry, Open);
        });
        PublicationOrder();
        var memory = new AdminBaselineRegistry(); Retry(memory, () => memory);
    }

    private static void PublicationOrder()
    {
        var slot = new FailingSlot(); var registry = new AdminBaselineRegistry(slot); registry.Submit(Request(10));
        var before = registry.Current; var policy = registry.BuildEffectiveTradeFeePolicy(Configuration); string? durable = slot.Read();
        slot.BeforeWrite = () =>
        {
            Require(ReferenceEquals(before, registry.Current) && ReferenceEquals(policy, registry.BuildEffectiveTradeFeePolicy(Configuration))
                && slot.Read() == durable, "Baseline or fee cache was published before persistence returned.");
        };
        var replacement = registry.Submit(Request(40));
        Require(ReferenceEquals(replacement, registry.Current) && !ReferenceEquals(policy, registry.BuildEffectiveTradeFeePolicy(Configuration)),
            "Successful baseline did not publish and invalidate its fee policy.");
        Require(Snapshot(new AdminBaselineRegistry(slot).Current) == Snapshot(replacement), "Published baseline differs from persisted baseline.");
    }

    private static void Unchanged(AdminBaselineRegistry registry, AdminBaselineSnapshot? before, TradeFeePolicy? policy, bool existing)
    {
        Require(ReferenceEquals(before, registry.Current), "Failed baseline write changed the current snapshot.");
        var currentPolicy = registry.BuildEffectiveTradeFeePolicy(Configuration);
        Require(policy is null || ReferenceEquals(policy, currentPolicy), "Failed baseline write invalidated the fee cache.");
        Require(currentPolicy.StandardMarketValuePerThing["Steel"] == (existing ? 10 : 3)
            && currentPolicy.CalculateRequiredFee([new("steel", "Steel", 10)]) == (existing ? 10 : 3),
            "Failed baseline changed the actual trade fee.");
    }

    private static void Retry(AdminBaselineRegistry registry, Func<AdminBaselineRegistry> reopen)
    {
        var submitted = registry.Submit(Request(40)); var policy = registry.BuildEffectiveTradeFeePolicy(Configuration);
        Require(ReferenceEquals(submitted, registry.Current) && policy.StandardMarketValuePerThing["Steel"] == 40
            && policy.CalculateRequiredFee([new("steel", "Steel", 10)]) == 40 && ReferenceEquals(policy, registry.BuildEffectiveTradeFeePolicy(Configuration)),
            "Baseline retry did not publish or cache the successful fee policy.");
        Require(Snapshot(reopen().Current) == Snapshot(submitted), "Reopened baseline differs after retry.");
        registry.Submit(Request(50));
        Require(!ReferenceEquals(policy, registry.BuildEffectiveTradeFeePolicy(Configuration))
            && registry.BuildEffectiveTradeFeePolicy(Configuration).StandardMarketValuePerThing["Steel"] == 50
            && Snapshot(reopen().Current) == Snapshot(registry.Current), "Later successful update retained stale fees or storage.");
    }

    private static SubmitAdminBaselineRequest Request(float price) => new(ProtocolApiVersion.Current, "admin", "admin-colony",
        DateTimeOffset.UnixEpoch.AddDays(price), [new("Steel", price), new("Silver", 1)], []);
    private static string Snapshot(AdminBaselineSnapshot? value) => JsonSerializer.Serialize(value);
    private static void ExpectFailure(Action run, params Type[] types)
    {
        try { run(); } catch (Exception ex) when (types.Any(t => t.IsInstanceOfType(ex))) { return; }
        throw new Exception("Baseline persistence failure was not injected.");
    }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-baseline-persistence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { run(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class FailingSlot : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public Action? BeforeWrite;
        public string? Read() => value;
        public void Write(string json) { BeforeWrite?.Invoke(); if (Fail) throw new IOException("Injected baseline persistence failure."); value = json; }
    }
}
