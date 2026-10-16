using System.Collections;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Data.Sqlite;

internal static class CompatibilityBaselinePersistenceTests
{
    private static readonly DateTimeOffset Before = DateTimeOffset.UnixEpoch.AddDays(1), After = Before.AddDays(1);
    public static void Run()
    {
        int count = 0; void Case(Action run) { run(); count++; }
        foreach (string operation in new[] { "ensure", "replace-empty", "replace" })
        {
            foreach (bool reopen in new[] { false, true })
            {
                Case(() => SlotFailure(operation, reopen));
                foreach (string phase in operation == "replace" ? new[] { "insert", "update" } : new[] { "insert" })
                    Case(() => SqliteFailure(operation, reopen, phase));
                if (OperatingSystem.IsWindows()) Case(() => FileFailure(operation, reopen));
            }
            Case(() => Publication(operation));
            Case(() => SerializationFailure(operation));
            Case(() => Memory(operation));
        }
        Case(NoOp); Case(ConcurrentEnsure); Case(ConcurrentReplace); Case(FailedFirstWriter);
        foreach (bool replace in new[] { false, true })
        foreach (string invalid in new[] { "manifest", "user", "whitespace" }) Case(() => Invalid(replace, invalid));
        Console.WriteLine($"Compatibility baseline persistence scenarios passed: {count}");
    }
    private static void SlotFailure(string operation, bool reopen)
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot); Seed(registry, operation);
        var before = registry.Current; string? durable = slot.Read(); slot.Fail = true;
        Throws(() => Write(registry, operation), typeof(IOException));
        Unchanged(registry, before); Require(slot.Read() == durable, "Failed slot write changed durable baseline.");
        Unchanged(new(slot), before, sameReference: false);
        slot.Fail = false; if (reopen) registry = new(slot); Retry(registry, operation, () => new(slot));
    }
    private static void SqliteFailure(string operation, bool reopen, string phase) => WithRoot(root =>
    {
        string database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database);
        CompatibilityBaselineRegistry Open() => new(new SqliteJsonPersistenceSlot(database, "compatibility-baseline"));
        var registry = Open(); Seed(registry, operation); var before = registry.Current;
        Sql(database, $"create trigger fail_compatibility before {phase} on server_documents when new.document_key = 'compatibility-baseline' begin select raise(abort, 'compatibility write'); end;");
        Throws(() => Write(registry, operation), typeof(SqliteException)); Unchanged(registry, before); Unchanged(Open(), before, false);
        Sql(database, "drop trigger fail_compatibility;"); if (reopen) registry = Open(); Retry(registry, operation, Open);
    });
    private static void FileFailure(string operation, bool reopen) => WithRoot(root =>
    {
        string path = Path.Combine(root, "baseline.json"); CompatibilityBaselineRegistry Open() => new(path);
        var registry = Open(); Seed(registry, operation); var before = registry.Current;
        if (operation == "replace")
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws(() => Write(registry, operation), typeof(IOException), typeof(UnauthorizedAccessException));
                Unchanged(registry, before); Require(bytes.SequenceEqual(File.ReadAllBytes(path)), "Failed file replacement changed baseline bytes."); Unchanged(Open(), before, false);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
            Throws(() => Write(registry, operation), typeof(IOException), typeof(UnauthorizedAccessException));
            Unchanged(registry, before); Unchanged(Open(), before, false); Directory.Delete(path);
        }
        if (reopen) registry = Open(); Retry(registry, operation, Open);
    });
    private static void Publication(string operation)
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot); Seed(registry, operation);
        var before = registry.Current; string? durable = slot.Read();
        slot.BeforeWrite = () => { Unchanged(registry, before); Require(slot.Read() == durable, "Store changed before write callback."); };
        var result = Write(registry, operation); slot.BeforeWrite = null;
        Check(registry, result, "new", "new-admin", After); Check(new(slot), result, "new", "new-admin", After, false);
    }
    private static void SerializationFailure(string operation)
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot); Seed(registry, operation);
        var before = registry.Current; string? durable = slot.Read(); int writes = slot.Writes;
        var manifest = Manifest("broken") with { DlcIds = new BrokenStrings() };
        Throws(() => Write(registry, operation, manifest), typeof(IOException));
        Unchanged(registry, before); Require(slot.Writes == writes && slot.Read() == durable, "Serialization failure reached storage.");
        Retry(registry, operation, () => new(slot));
    }
    private static void NoOp()
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot); Seed(registry, "replace");
        var before = registry.Current; int writes = slot.Writes; slot.Fail = true;
        var result = registry.EnsureBaseline(Manifest("ignored") with { DlcIds = new BrokenStrings() }, "ignored", After);
        Require(!result.Updated && slot.Writes == writes, "Existing baseline was serialized or persisted again.");
        Check(registry, result, "old", "old-admin", Before);
    }
    private static void Memory(string operation)
    { var registry = new CompatibilityBaselineRegistry(); Seed(registry, operation); Retry(registry, operation, () => registry); }
    private static void Invalid(bool replace, string invalid)
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot); Seed(registry, "replace"); var before = registry.Current; string? durable = slot.Read();
        Throws(() => Write(registry, replace ? "replace" : "ensure", invalid == "manifest" ? null : Manifest("invalid"),
            invalid == "user" ? "" : invalid == "whitespace" ? " \t" : "actor", useDefault: false), typeof(ArgumentException));
        Unchanged(registry, before); Require(slot.Read() == durable, "Invalid baseline request changed persistence.");
    }
    private static void ConcurrentEnsure()
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => registry.EnsureBaseline(Manifest("m" + i), "a" + i, Before.AddMinutes(i)))).ToArray();
        Task.WaitAll(tasks); var winner = tasks.Single(t => t.Result.Updated).Result;
        Require(slot.Writes == 1 && tasks.All(t => t.Result.Baseline == winner.Baseline && t.Result.UpdatedByUserId == winner.UpdatedByUserId && t.Result.UpdatedAtUtc == winner.UpdatedAtUtc), "Concurrent first writers disagree on the persisted winner.");
        Check(new(slot), winner, winner.Baseline!.ManifestId, winner.UpdatedByUserId!, winner.UpdatedAtUtc!.Value, false);
    }
    private static void ConcurrentReplace()
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => registry.ReplaceBaseline(Manifest("m" + i), "a" + i, Before.AddMinutes(i)))).ToArray();
        Task.WaitAll(tasks); var final = tasks.Single(t => ReferenceEquals(t.Result.Baseline, registry.Current)).Result;
        Require(slot.Writes == 8 && tasks.All(t => t.Result.Updated), "Concurrent replacements lost a write.");
        Check(new(slot), final, final.Baseline!.ManifestId, final.UpdatedByUserId!, final.UpdatedAtUtc!.Value, false);
    }
    private static void FailedFirstWriter()
    {
        var slot = new Slot(); var registry = new CompatibilityBaselineRegistry(slot);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        slot.BeforeWrite = () => { entered.Set(); Require(release.Wait(TimeSpan.FromSeconds(10)), "Writer was not released."); throw new IOException("First writer failed."); };
        Task first = Task.Run(() => Throws(() => Write(registry, "ensure"), typeof(IOException)));
        try { Require(entered.Wait(TimeSpan.FromSeconds(10)), "First write did not start."); }
        finally { release.Set(); }
        first.GetAwaiter().GetResult(); slot.BeforeWrite = null;
        var second = Task.Run(() => registry.EnsureBaseline(Manifest("second"), "second-admin", After.AddDays(1))).GetAwaiter().GetResult();
        Require(second.Updated && slot.Writes == 1, "Failed first write reserved the initial baseline or leaked its gate.");
        Check(new(slot), second, "second", "second-admin", After.AddDays(1), false);
    }
    private static void Seed(CompatibilityBaselineRegistry registry, string operation)
    { if (operation == "replace") registry.EnsureBaseline(Manifest("old"), "old-admin", Before); }
    private static CompatibilityBaselineUpdateResult Write(CompatibilityBaselineRegistry registry, string operation, CompatibilityManifest? manifest = null, string user = "new-admin", bool useDefault = true)
        => operation == "ensure" ? registry.EnsureBaseline(useDefault ? manifest ?? Manifest("new") : manifest!, user, After)
            : registry.ReplaceBaseline(useDefault ? manifest ?? Manifest("new") : manifest!, user, After);
    private static CompatibilityManifest Manifest(string id) => new() { ManifestId = id, ProtocolVersion = "test", RimWorldVersion = "1.7", DlcIds = ["core"] };
    private static void Retry(CompatibilityBaselineRegistry registry, string operation, Func<CompatibilityBaselineRegistry> reopen)
    { var result = Write(registry, operation); Require(result.Updated, "Retry did not publish baseline."); Check(registry, result, "new", "new-admin", After); Check(reopen(), result, "new", "new-admin", After, false); }
    private static void Unchanged(CompatibilityBaselineRegistry registry, CompatibilityManifest? before, bool sameReference = true)
    {
        Require(sameReference ? ReferenceEquals(registry.Current, before) : JsonSerializer.Serialize(registry.Current) == JsonSerializer.Serialize(before), "Failed baseline write changed the current manifest.");
        if (before is not null) Check(registry, registry.EnsureBaseline(Manifest("ignored"), "ignored", After), "old", "old-admin", Before);
    }
    private static void Check(CompatibilityBaselineRegistry registry, CompatibilityBaselineUpdateResult result, string id, string actor, DateTimeOffset at, bool sameReference = true)
    {
        Require(result.Baseline?.ManifestId == id && result.UpdatedByUserId == actor && result.UpdatedAtUtc == at
            && (sameReference ? ReferenceEquals(registry.Current, result.Baseline) : JsonSerializer.Serialize(registry.Current) == JsonSerializer.Serialize(result.Baseline)), "Published baseline and metadata disagree.");
        var current = registry.EnsureBaseline(Manifest("ignored"), "ignored", After.AddDays(5));
        Require(!current.Updated && current.UpdatedByUserId == actor && current.UpdatedAtUtc == at, "Baseline metadata did not survive reload.");
    }
    private static void Throws(Action run, params Type[] types)
    { try { run(); } catch (Exception ex) when (types.Any(t => t.IsInstanceOfType(ex))) { return; } throw new Exception("Expected baseline failure was not observed."); }
    private static void Sql(string database, string sql)
    { using var c = new SqliteConnection("Data Source=" + database); c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> run)
    { string root = Path.Combine(Path.GetTempPath(), "cor-compatibility-persistence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { run(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } }
    private sealed class Slot : IJsonPersistenceSlot
    {
        private string? value; public bool Fail; public int Writes; public Action? BeforeWrite;
        public string? Read() => value;
        public void Write(string json) { BeforeWrite?.Invoke(); if (Fail) throw new IOException("Injected baseline write failure."); value = json; Writes++; }
    }
    private sealed class BrokenStrings : IReadOnlyList<string>
    {
        public int Count => 1; public string this[int index] => throw new IOException("Injected serialization failure.");
        public IEnumerator<string> GetEnumerator() => throw new IOException("Injected serialization failure.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
