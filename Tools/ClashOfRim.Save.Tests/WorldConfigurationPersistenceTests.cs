using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class WorldConfigurationPersistenceTests
{
    private static readonly string[] Stores = ["memory", "file", "sqlite-json", "sqlite-structured", "mixed"];
    public static void Run()
    {
        int cases = 0;
        void Case(Action test) { test(); cases++; }
        foreach (string storage in Stores)
        {
            foreach (string action in new[] { "reset", "substrate", "promote", "revoke", "catalog", "prepare", "first-submit" })
                Case(() => MetadataFailure(storage, action));
            foreach (bool after in new[] { false, true }) Case(() => BlobFailure(storage, after));
            foreach (string action in new[] { "reset", "substrate" }) Case(() => CrashAfterMetadata(storage, action));
            foreach (string corruption in new[] { "missing", "hash", "type", "key", "configuration", "json", "package", "geometry" })
                Case(() => Corruption(storage, corruption));
            Case(() => Legacy(storage));
            Case(() => ImmutableConflict(storage));
        }
        foreach (string action in new[] { "reset", "substrate" })
        foreach (string storage in new[] { "file", "sqlite-structured" })
        foreach (string stage in new[] { "before-metadata", "after-metadata" }) Case(() => ProcessCrash(storage, action, stage));
        foreach (string failure in new[] { "json-update", "records-insert", "records-delete", "blob-insert" }) Case(() => SqlFailure(failure));
        if (OperatingSystem.IsWindows()) Case(FileFailure);
        Case(Migration);
        Console.WriteLine($"World persistence scenarios passed: {cases}");
    }

    private static void MetadataFailure(string storage, string action)
    {
        using var f = new Fixture(storage); var world = f.Open();
        if (action is not ("prepare" or "first-submit")) Seed(world);
        string before = Capture(world), durable = f.Metadata(); var current = world.Current; int writes = f.Faults.BlobWrites;
        f.Faults.BeforeMetadata = () => Require(Capture(world) == before && Capture(f.Open()) == before, "Candidate published before metadata committed.");
        f.Faults.FailMetadata = true;
        Expect(() => Apply(world, action), typeof(IOException));
        Require(ReferenceEquals(current, world.Current) && Capture(world) == before && Capture(f.Open()) == before
            && f.Metadata() == durable, "Failed world write changed active or durable state: " + storage + "/" + action);
        f.Faults.FailMetadata = false;
        string? published = null; f.Faults.AfterMetadata = () => published = Capture(f.Open());
        Apply(world, action);
        Require(published == Capture(world) && Capture(f.Open()) == Capture(world), "Publication differs from its durable record.");
        AssertApplied(world, action);
        if (action != "substrate") Require(f.Faults.BlobWrites == writes, "Metadata-only update rewrote a substrate blob.");
    }

    private static void BlobFailure(string storage, bool after)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world);
        string before = Capture(world), durable = f.Metadata();
        f.Faults.BeforeBlob = () => Require(Capture(world) == before, "Blob candidate was published before storage.");
        f.Faults.FailBlob = !after;
        if (after) f.Faults.AfterBlob = () => throw new SimulatedCrash();
        Expect(() => Apply(world, "substrate"), after ? typeof(SimulatedCrash) : typeof(IOException));
        Require(Capture(world) == before && Capture(f.Open()) == before && f.Metadata() == durable,
            "Interrupted blob write replaced the referenced world.");
        f.Faults.Clear(); Apply(world, "substrate"); AssertApplied(world, "substrate");
        Require(Capture(f.Open()) == Capture(world), "Blob retry did not publish a consistent world.");
        int writes = f.Faults.BlobWrites; Apply(world, "substrate");
        Require(f.Faults.BlobWrites == writes, "Identical substrate was not reused.");
    }

    private static void CrashAfterMetadata(string storage, string action)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world);
        string? committed = null;
        f.Faults.AfterMetadata = () => { committed = Capture(f.Open()); throw new SimulatedCrash(); };
        Expect(() => Apply(world, action), typeof(SimulatedCrash));
        // A stopped process cannot roll back a committed metadata reference.
        f.Faults.Clear(); var reopened = f.Open(); AssertApplied(reopened, action);
        Require(Capture(reopened) == committed, "Restart lost a committed world reference.");
    }

    private static void Corruption(string storage, string corruption)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world);
        string key = f.Reference()!;
        if (corruption == "missing")
        { f.Binary.WriteBinary(WorldSubstratePersistence.LegacyKey, f.Binary.ReadBinary(key)); f.Binary.WriteBinary(key, null); }
        if (corruption == "hash") f.Binary.WriteBinary(key, Payload());
        if (corruption is "type" or "key") f.ChangeState(state => state["WorldSubstrateKey"] = corruption == "type" ? JsonValue.Create(42) : JsonValue.Create("../outside"));
        if (corruption == "configuration") f.RemoveConfiguration();
        if (corruption == "json") f.WriteState("{");
        if (corruption is "package" or "geometry")
        {
            byte[] bytes = corruption == "package" ? [1, 2, 3] : WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(1, "<grid />", "<features />", "<landmarks />", []));
            string invalidKey = WorldSubstratePersistence.KeyFor(bytes); f.Binary.WriteBinary(invalidKey, bytes);
            f.ChangeState(state => state["WorldSubstrateKey"] = invalidKey);
        }
        string evidence = f.Metadata();
        Expect(() => f.Open(), typeof(InvalidOperationException));
        Require(f.Metadata() == evidence, "Corruption handling rewrote world metadata.");
    }

    private static void Legacy(string storage)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world);
        world.TryGetWorldSubstrate("world-stage-test", out var oldBlob); string expected = Capture(world);
        f.Binary.WriteBinary(WorldSubstratePersistence.LegacyKey, oldBlob);
        f.ChangeState(state => state.Remove("WorldSubstrateKey"));
        world = f.Open(); Require(Capture(world) == expected, "Legacy fixed-key substrate was not restored.");
        f.Faults.FailMetadata = true; Expect(() => Apply(world, "substrate"), typeof(IOException));
        Require(Capture(world) == expected && Capture(f.Open()) == expected, "Failed legacy upgrade replaced the fixed-key blob.");
        f.Faults.Clear(); Apply(world, "substrate"); AssertApplied(f.Open(), "substrate");
        Require(f.Binary.ReadBinary(WorldSubstratePersistence.LegacyKey)!.SequenceEqual(oldBlob!), "Upgrade rewrote legacy substrate content.");
        Apply(world, "reset"); Require(f.Reference() == "" && f.Open().Current is null, "Explicit reset resurrected the legacy substrate.");
    }

    private static void ImmutableConflict(string storage)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world);
        string before = Capture(world), durable = f.Metadata();
        world.TryGetWorldSubstrate("world-stage-test", out var original);
        string key = WorldSubstratePersistence.KeyFor(Payload()); f.Binary.WriteBinary(key, original);
        Expect(() => Apply(world, "substrate"), typeof(InvalidOperationException));
        Require(Capture(world) == before && Capture(f.Open()) == before && f.Metadata() == durable
            && f.Binary.ReadBinary(key)!.SequenceEqual(original!), "Conflicting immutable key was replaced or published.");
        f.Binary.WriteBinary(key, null); Apply(world, "substrate"); AssertApplied(f.Open(), "substrate");
    }

    private static void ProcessCrash(string storage, string action, string stage)
    {
        using var f = new Fixture(storage); var world = f.Open(); Seed(world); string before = Capture(world);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(WorldConfigurationPersistenceTests).Assembly.Location);
        foreach (string arg in new[] { "--world-publication-crash", f.Root, storage, action, stage }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync(); var output = child.StandardOutput.ReadToEndAsync();
        if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); throw new Exception("World crash child timed out."); }
        Require(child.ExitCode == (stage == "before-metadata" ? 73 : 74), "Crash child did not reach its checkpoint: " + error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
        var reopened = f.Open();
        if (stage == "before-metadata") Require(Capture(reopened) == before, "Process exit before metadata changed the active world.");
        else AssertApplied(reopened, action);
        Apply(reopened, action); AssertApplied(f.Open(), action);
    }

    internal static void CrashChild(string[] args)
    {
        using var f = new Fixture(args[2], args[1]); var world = f.Open();
        if (args[4] == "before-metadata") f.Faults.BeforeMetadata = () => Environment.Exit(73);
        else f.Faults.AfterMetadata = () => Environment.Exit(74);
        Apply(world, args[3]); throw new Exception("Child did not stop at its persistence checkpoint.");
    }

    private static void SqlFailure(string failure)
    {
        using var f = new Fixture(failure == "json-update" ? "sqlite-json" : "sqlite-structured"); var world = f.Open(); Seed(world);
        string before = Capture(world), durable = f.Metadata();
        string table = failure == "json-update" ? "server_documents" : failure == "blob-insert" ? "server_binary_documents" : "server_keyed_json_records";
        string verb = failure == "records-delete" ? "delete" : failure == "json-update" ? "update" : "insert";
        Sql(f.Database, $"create trigger fail_world before {verb} on {table} begin select raise(abort, 'world failure'); end;");
        string action = failure == "blob-insert" ? "substrate" : "reset";
        Expect(() => Apply(world, action), typeof(SqliteException));
        Require(Capture(world) == before && Capture(f.Open()) == before && f.Metadata() == durable, "SQLite failure partially published a world.");
        Sql(f.Database, "drop trigger fail_world;"); Apply(world, action); AssertApplied(f.Open(), action);
    }

    private static void FileFailure()
    {
        using var f = new Fixture("file"); var world = f.Open(); Seed(world); string before = Capture(world);
        using (var held = new FileStream(f.JsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Expect(() => Apply(world, "substrate"), typeof(IOException), typeof(UnauthorizedAccessException));
            Require(Capture(world) == before && Capture(f.Open()) == before, "Locked metadata file lost its prior substrate.");
        }
        Apply(world, "substrate"); AssertApplied(f.Open(), "substrate");
    }

    private static void Migration()
    {
        using var f = new Fixture("sqlite-structured"); var world = f.Open(); Seed(world);
        world.TryGetWorldSubstrate("world-stage-test", out var bytes); f.Binary.WriteBinary(WorldSubstratePersistence.LegacyKey, bytes);
        f.ChangeState(state => state.Remove("WorldSubstrateKey")); string before = Capture(f.Open());
        Sql(f.Database, "update server_schema_metadata set metadata_value='27' where metadata_key='schema_version';");
        var startup = new ServerPersistenceMigrationService(f.Root).PrepareForStartup();
        Require(startup.Migration is not null, "Version 27 did not require the world-reference migration.");
        var migration = startup.Migration!;
        Require(migration.Database.AppliedMigrations.Contains("27->28") && migration.Database.FinalVersion == ServerDatabaseSchema.CurrentVersion
            && !string.IsNullOrWhiteSpace(migration.BackupDirectory) && Directory.Exists(migration.BackupDirectory)
            && Capture(f.Open()) == before, "Versioned world-reference migration lost legacy state or backup.");
        world = f.Open(); Apply(world, "substrate"); AssertApplied(f.Open(), "substrate");
    }

    private static void Seed(WorldConfigurationRegistry world) { ColonyDeletionStageTests.SeedWorld(world); world.PromoteAdministrator("second"); }
    private static void Apply(WorldConfigurationRegistry world, string action)
    {
        switch (action)
        {
            case "reset": case "first-submit": world.Submit("admin", Configuration()); break;
            case "substrate": Require(world.StoreWorldSubstrate("admin", "admin-colony", "world-stage-test", Payload()).Accepted, "Substrate was rejected."); break;
            case "prepare": world.Prepare("admin"); break;
            case "promote": Require(world.PromoteAdministrator("third"), "Promotion failed."); break;
            case "revoke": Require(world.RevokeAdministrator("admin"), "Revocation failed."); break;
            case "catalog": Require(world.SubmitWorldFeatureNames("admin", "admin-colony", "French", "world-stage-test", []).Created, "Catalog was not created."); break;
            default: throw new Exception("Unknown mutation.");
        }
    }
    private static void AssertApplied(WorldConfigurationRegistry world, string action)
    {
        if (action is "reset" or "first-submit") Require(world.Current is null && Pending(world)?.WorldConfigurationId == "new-world", "Reset did not preserve the pending new configuration.");
        if (action == "substrate") Require(world.Current is not null && world.TryGetWorldSubstrate("world-stage-test", out var bytes) && bytes!.SequenceEqual(Payload()), "New substrate did not survive reopening.");
        if (action == "prepare") Require(world.IsAdministrator("admin"), "First administrator was not persisted.");
        if (action == "promote") Require(world.IsAdministrator("third"), "Promotion was not persisted.");
        if (action == "revoke") Require(!world.IsAdministrator("admin") && world.GetSessionState("second").AdministratorUserId == "second", "Revocation or primary administrator was lost.");
        if (action == "catalog") Require(world.Current!.FeatureNameCatalogs.Any(c => c.Language == "French"), "Catalog was not persisted.");
    }
    private static WorldConfigurationDto? Pending(WorldConfigurationRegistry world) => (WorldConfigurationDto?)typeof(WorldConfigurationRegistry).GetField("worldConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world);
    private static string Capture(WorldConfigurationRegistry world) => JsonSerializer.Serialize(new { configuration = Pending(world), current = world.Current,
        admins = world.ListAdministrators(), session = world.GetSessionState("admin"), notifications = world.ListPendingLayerNotifications(),
        substrate = (byte[]?)typeof(WorldConfigurationRegistry).GetField("worldSubstratePayload", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world) });
    private static WorldConfigurationDto Configuration() => new("new-world", "admin", "admin-colony", DateTimeOffset.UnixEpoch,
        "new-seed", "0.3", "Normal", "Normal", "Normal", "Normal", "10", ["OutlanderCivil"], [], [], [], [], []);
    private static byte[] Payload() => WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(2, "<grid />", "<features />", "<landmarks />",
        WorldTileGeometryBinaryCodec.Encode(new WorldTileGeometryDto([new WorldTileLayerGeometryDto(0, "Surface", 1, [new WorldTileCenterDto(0, 2, 0, 0)])]))!));
    private static void Expect(Action action, params Type[] types)
    { try { action(); } catch (Exception ex) when (types.Any(t => t.IsInstanceOfType(ex))) { return; } throw new Exception("Expected storage failure was not reached."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Sql(string path, string text) { using var c = new SqliteConnection("Data Source=" + path); c.Open(); using var q = c.CreateCommand(); q.CommandText = text; q.ExecuteNonQuery(); }
    private sealed class SimulatedCrash : Exception;

    private sealed class Faults
    {
        public bool FailMetadata, FailBlob; public int BlobWrites;
        public Action? BeforeMetadata, AfterMetadata, BeforeBlob, AfterBlob;
        public void Metadata(Action write) { BeforeMetadata?.Invoke(); if (FailMetadata) throw new IOException("Metadata failure."); write(); AfterMetadata?.Invoke(); }
        public void Blob(Action write) { BeforeBlob?.Invoke(); if (FailBlob) throw new IOException("Blob failure."); write(); BlobWrites++; AfterBlob?.Invoke(); }
        public void Clear() { FailMetadata = FailBlob = false; BeforeMetadata = AfterMetadata = BeforeBlob = AfterBlob = null; }
    }
    private sealed class JsonProxy(IJsonPersistenceSlot inner, Faults faults) : IJsonPersistenceSlot
    { public string? Read() => inner.Read(); public void Write(string json) => faults.Metadata(() => inner.Write(json)); }
    private sealed class BinaryProxy(IBinaryPersistenceSlot inner, Faults faults) : IBinaryPersistenceSlot
    { public byte[]? ReadBinary(string key) => inner.ReadBinary(key); public void WriteBinary(string key, byte[]? bytes) => faults.Blob(() => inner.WriteBinary(key, bytes)); }
    private sealed class RecordsProxy(IKeyedJsonRecordStore inner, Faults faults) : IKeyedJsonRecordStore
    {
        public bool IsInitialized() => inner.IsInitialized(); public IReadOnlyDictionary<string, string> ReadAll() => inner.ReadAll();
        public void ReplaceAll(IReadOnlyDictionary<string, string> rows) => faults.Metadata(() => inner.ReplaceAll(rows));
        public void ReplaceAllForImport(IReadOnlyDictionary<string, string> rows) => ReplaceAll(rows);
        public void ApplyBatch(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes) => faults.Metadata(() => inner.ApplyBatch(upserts, deletes));
    }
    private sealed class MemoryJson : IJsonPersistenceSlot
    { private string? value; public string? Read() => value; public void Write(string json) => value = json; }
    internal sealed class MemoryBinary : IBinaryPersistenceSlot
    {
        private readonly Dictionary<string, byte[]> values = new();
        public byte[]? ReadBinary(string key) => values.TryGetValue(key, out var bytes) ? bytes.ToArray() : null;
        public void WriteBinary(string key, byte[]? bytes) { if (bytes is null) values.Remove(key); else values[key] = bytes.ToArray(); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root; public string Database => Path.Combine(Root, "server.sqlite"); public string JsonPath => Path.Combine(Root, "world.json");
        public readonly Faults Faults = new(); public readonly IBinaryPersistenceSlot Binary;
        private readonly IJsonPersistenceSlot? json; private readonly IKeyedJsonRecordStore? records; private readonly bool ownsRoot;
        public Fixture(string storage, string? root = null)
        {
            ownsRoot = root is null; Root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "cor-world-persistence-" + Guid.NewGuid().ToString("N")));
            Require(Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                && Path.GetFileName(Root).StartsWith("cor-world-persistence-", StringComparison.Ordinal), "Unsafe test directory.");
            Directory.CreateDirectory(Root);
            if (storage is "sqlite-json" or "sqlite-structured" or "mixed") ServerDatabaseMigrator.Migrate(Database);
            if (storage is "sqlite-structured" or "mixed") records = new SqliteKeyedJsonRecordStore(Database, "world-configuration");
            else json = storage == "memory" ? new MemoryJson() : storage == "file" ? new FileJsonPersistenceSlot(JsonPath) : new SqliteJsonPersistenceSlot(Database, "world-configuration");
            Binary = storage == "memory" ? new MemoryBinary() : storage is "file" or "mixed" ? new FileBinaryPersistenceSlot(Path.Combine(Root, "binary")) : new SqliteJsonPersistenceSlot(Database, "world-configuration");
        }
        public WorldConfigurationRegistry Open() => new(records is null ? null : new RecordsProxy(records, Faults),
            json is null ? null : new JsonProxy(json, Faults), new BinaryProxy(Binary, Faults), null);
        public string Metadata() => records is null ? json!.Read() ?? "" : JsonSerializer.Serialize(records.ReadAll());
        private string State() => records is null ? json!.Read()! : records.ReadAll()["state"];
        public string? Reference() => WorldSubstratePersistence.ReadKey(State());
        public void ChangeState(Action<JsonObject> edit) { var value = JsonNode.Parse(State())!.AsObject(); edit(value); WriteState(value.ToJsonString()); }
        public void WriteState(string value)
        { if (records is null) json!.Write(value); else { var rows = records.ReadAll().ToDictionary(); rows["state"] = value; records.ReplaceAll(rows); } }
        public void RemoveConfiguration()
        { if (records is null) ChangeState(state => state["WorldConfiguration"] = null); else { var rows = records.ReadAll().ToDictionary(); rows.Remove("core"); records.ReplaceAll(rows); } }
        public void Dispose() { if (ownsRoot) { SqliteConnection.ClearAllPools(); Directory.Delete(Root, recursive: true); } }
    }
}
