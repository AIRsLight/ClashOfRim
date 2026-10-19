using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class SupportCommitTests
{
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");
    private static readonly ProtocolIdentity Target = new("userB", "colonyB", "target");
    private const string PawnKey = "owner:userA/colony:colonyA/snapshot:before/pawn:Pawn_42";
    private const string Xml = "<saveable Class=\"Pawn\"><id>Pawn_42</id><kindDef>Colonist</kindDef></saveable>";
    private static PawnExchangePackage Pawn => new(1, new(PawnKey, "before", "support", false, "PlayerColony"),
        new("Human", "Colonist", "PlayerColony", "Female"), new("support", null, null, null, null, null, null),
        new(false, 1000, 1000, null, "Mobile"), [], [], [], new(Xml, SafePawnExchangeSerializer.ComputeScribeXmlSha256(Xml), []));
    internal static CreateSupportPawnWithSnapshotRequest Request(SaveSnapshotPackage package, string key = "support")
    {
        var pawn = JsonSerializer.Deserialize<PawnExchangePackageDto>(JsonSerializer.Serialize(Pawn))!;
        return new(new CreateSupportPawnRequest(key, Actor, Target, PawnKey, "before", "support", true, null,
            pawn.Reference, pawn, new EventTargetContextDto("target-world", "target-map", 1, "MapEdge"),
            2, "Caravan_1", false, 7, 420000, false), ProtocolDtoMapper.ToMetadataDto(package));
    }

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in new[] { "memory", "file", "sqlite" })
            WithDirectory(root => Boundaries(fixture, root, kind).GetAwaiter().GetResult());
        foreach (bool reopen in new[] { false, true })
        foreach (string phase in new[] { "before-snapshot", "after-snapshot", "sql", "notify", "post-upload" })
            WithDirectory(root => Crash(fixture, root, phase, reopen));
        foreach (string conflict in new[] { "newer-snapshot", "same-id-payload", "same-key-id", "same-id-key", "journal-owner", "journal-version", "journal-mixed", "journal-status" })
            WithDirectory(root => RecoveryConflict(fixture, root, conflict));
        WithDirectory(root => EndpointStorageFailure(fixture, root).GetAwaiter().GetResult());
        foreach (bool sameKey in new[] { false, true }) WithDirectory(root => Concurrent(fixture, root, sameKey).GetAwaiter().GetResult());
        foreach (bool replaced in new[] { false, true }) WithDirectory(root => RevokedWhileWaiting(fixture, root, replaced));
        WithDirectory(root => Migration(fixture, root));
    }

    private static ClashOfRimNetworkState Open(string root, string kind = "sqlite", IColonySnapshotIndexStore? snapshots = null) => new(
        ledger: kind == "memory" ? new InMemoryAuthoritativeEventLedger() : kind == "file"
            ? new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")) : new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        snapshotStore: snapshots ?? new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
        snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")));

    internal static SaveSnapshotPackage Prepare(Func<string, SaveSnapshotPackage> fixture, ClashOfRimNetworkState state, string root)
    {
        var before = fixture("before");
        before = before with { Envelope = before.Envelope with { NextLineageToken = "parent-token" } };
        if (state.SnapshotStore is IColonySnapshotPackageStore packages) packages.StoreLatest(before, before.Index, DateTimeOffset.UnixEpoch);
        else state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(before.Envelope.Identity, before.Envelope, before.Index, DateTimeOffset.UnixEpoch));
        var targetId = new SnapshotIdentity("userB", "colonyB", "target");
        state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(targetId, before.Envelope with { Identity = targetId }, before.Index, DateTimeOffset.UnixEpoch));
        using var zip = new GZipStream(new MemoryStream(before.Payload), CompressionMode.Decompress);
        var xml = XDocument.Load(zip); var game = xml.Root!.Element("game")!;
        var components = game.Element("components");
        if (components is null) { components = new XElement("components"); game.Add(components); }
        components.Elements("li").Where(e => e.Attribute("Class")?.Value.Contains("ClashOfRimGameComponent") == true).Remove();
        components.Add(new XElement("li", new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
            new XElement("clashOfRimLineageSnapshotId", "before"), new XElement("clashOfRimLineageToken", "parent-token")));
        string path = Path.Combine(root, "support.rws"); xml.Save(path);
        var package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity("userA", "colonyA", "after"), DateTimeOffset.UnixEpoch, SnapshotPayloadEncoding.GzipRws);
        return package with { Envelope = package.Envelope with { PreviousSnapshotId = "before", LineageToken = "parent-token" } };
    }

    private static Task<EventCreationResponse> Invoke(ClashOfRimNetworkState state, CreateSupportPawnWithSnapshotRequest request,
        byte[] bytes, ProtocolIdentity? principal = null) => SnapshotEndpointTestClient.Invoke<EventCreationResponse>(
            "CreateSupportPawnWithSnapshot", state, request, bytes, principal ?? Actor);

    private static async Task Boundaries(Func<string, SaveSnapshotPackage> fixture, string root, string kind)
    {
        var state = Open(root, kind); var package = Prepare(fixture, state, root); var request = Request(package);
        var created = await Invoke(state, request, package.Payload);
        Check(created.Result.Accepted && created.AppliedSnapshotId == "after" && state.Ledger.ListAll().Count == 1, "support creation failed: " + JsonSerializer.Serialize(created));
        if (kind != "memory") state = Open(root, kind);
        var source = state.Ledger.Find(created.EventId!)!;
        foreach (string path in new[] { "SupportPawn.Actor.UserId", "SupportPawn.Actor.ColonyId", "SupportPawn.Actor.SnapshotId",
            "SupportPawn.Target.UserId", "SupportPawn.Target.ColonyId", "SupportPawn.Target.SnapshotId", "SupportPawn.SourceSnapshotId",
            "SupportPawn.PawnName", "SupportPawn.SourceCaravanLoadId", "SupportPawn.TargetContext.MapUniqueId",
            "SupportPawn.TargetContext.WorldObjectId", "SupportPawn.PawnPackage.Appearance.DisplayName",
            "ConfirmedSnapshot.OwnerId", "ConfirmedSnapshot.ColonyId", "ConfirmedSnapshot.SnapshotId",
            "ConfirmedSnapshot.PayloadSha256", "ConfirmedSnapshot.OriginalSha256", "ConfirmedSnapshot.LineageToken" })
        {
            var changed = Change(request, path, "changed");
            string before = State(state); long version = state.EventNotifications.GetVersion("userB");
            var response = await Invoke(state, changed, package.Payload, changed.SupportPawn.Actor);
            Check(!response.Result.Accepted && response.EventId is null && response.AppliedSnapshotId is null
                && State(state) == before && version == state.EventNotifications.GetVersion("userB"), "conflicting request accepted: " + path);
        }
        foreach (string path in new[] { "SupportPawn.TemporaryControl", "SupportPawn.PermanentSupport", "SupportPawn.AutoReturnOnSettlement" })
        {
            bool current = path.EndsWith("TemporaryControl") ;
            var response = await Invoke(state, Change(request, path, !current), package.Payload);
            Check(!response.Result.Accepted, "changed support policy accepted");
        }
        Check(!(await Invoke(state, Change(request, "SupportPawn.SupportDurationDays", 8), package.Payload)).Result.Accepted, "changed duration accepted");
        Check(!(await Invoke(state, Change(request, "SupportPawn.ExpiresAtGameTicks", 420001L), package.Payload)).Result.Accepted, "changed expiry accepted");
        Check(!(await Invoke(state, Change(request, "SupportPawn.SourceTile", 4), package.Payload)).Result.Accepted, "changed return tile accepted");
        Check(!(await Invoke(state, request, [1, 2, 3])).Result.Accepted, "changed snapshot bytes accepted on retry");
        Check((await Invoke(state, request, package.Payload, Target)).Result.ErrorCode == ProtocolErrorCode.Unauthorized, "forged sender accepted");
        // All consumed/closed states retain their metadata and never recreate a pawn on a legitimate retry.
        foreach (var status in Enum.GetValues<ServerEventStatus>())
        {
            state.Ledger.ChangeStatus(source.EventId, status);
            string before = State(state); long version = state.EventNotifications.GetVersion("userB");
            var replay = await Invoke(state, request, package.Payload);
            Check(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.EventId == source.EventId
                && before == State(state) && state.EventNotifications.GetVersion("userB") > version, "retry rewrote consumed support or lost wake-up");
        }
        foreach (var corrupt in new[] {
            source with { Type = ServerEventType.ItemDelivery },
            source with { Target = new EventParty("wrong", "colonyB") },
            source with { Payload = ((SupportPawnEventPayload)source.Payload) with { CreationRequestHash = null } },
            source with { Payload = ((SupportPawnEventPayload)source.Payload) with { PawnName = "changed" } },
            source with { Payload = ((SupportPawnEventPayload)source.Payload) with { ReturnToSender = true } } })
        {
            var isolated = new ClashOfRimNetworkState(); isolated.Ledger.Append(corrupt);
            Check((await Invoke(isolated, request, package.Payload)).Result.ErrorCode == ProtocolErrorCode.Conflict, "cross-operation or corrupt/legacy binding accepted");
        }
    }

    private static (SaveSnapshotPackage Package, LatestSnapshotRecord Snapshot, SupportSnapshotMutation Mutation) Plan(
        Func<string, SaveSnapshotPackage> fixture, string root, ClashOfRimNetworkState state)
    {
        var package = Prepare(fixture, state, root);
        // Use a successful endpoint in an isolated state to obtain exactly the production event and validated binding.
        var seed = new ClashOfRimNetworkState(snapshotStore: new InMemoryColonySnapshotIndexStore());
        Prepare(fixture, seed, root);
        var response = Invoke(seed, Request(package), package.Payload).GetAwaiter().GetResult();
        Check(response.Result.Accepted, "plan fixture failed: " + JsonSerializer.Serialize(response));
        var snapshot = seed.SnapshotStore.GetLatest("userA", "colonyA")!;
        return (package, snapshot, new SupportSnapshotMutation(seed.Ledger.Find(response.EventId!)!));
    }

    private static void Crash(Func<string, SaveSnapshotPackage> fixture, string root, string phase, bool reopen)
    {
        var state = Open(root); var plan = Plan(fixture, root, state);
        if (phase == "after-snapshot") PendingBankTestDebt.AddAncillary(state.BankLoans);
        if (phase == "sql") Sql(root, "create trigger fail_support before insert on events begin select raise(abort, 'support fault'); end;");
        var store = new FaultStore((IColonySnapshotPackageStore)state.SnapshotStore, phase);
        bool failed = false;
        try { state.SnapshotCommits.CommitSupport(store, state.BankLoans, state.Ledger, plan.Package, plan.Snapshot, plan.Mutation,
            (_, _) => { if (phase == "post-upload") throw new IOException("post-upload"); },
            _ => { if (phase == "notify") throw new IOException("notify"); }); }
        catch (IOException) { failed = true; }
        catch (SqliteException) when (phase == "sql") { failed = true; }
        Check(failed && Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Length == 1, "interruption lost intent");
        if (phase == "sql") { Check(state.Ledger.ListAll().Count == 0, "failed SQL inserted support"); Sql(root, "drop trigger fail_support"); }
        if (phase is "notify" or "post-upload")
        {
            state.Ledger.MarkDelivered(plan.Mutation.Support.EventId, "consumed", DateTimeOffset.UtcNow);
            state.Ledger.MarkApplied(plan.Mutation.Support.EventId, "consumed", DateTimeOffset.UtcNow);
        }
        string? consumed = phase is "notify" or "post-upload" ? JsonSerializer.Serialize(state.Ledger.ListAll()) : null;
        var bank = state.BankLoans;
        if (reopen) state = Open(root);
        int wakes = 0, posts = 0;
        state.SnapshotCommits.Recover(state.SnapshotStore, bank, (_, _) => posts++, state.Ledger, _ => wakes++);
        state.SnapshotCommits.Recover(state.SnapshotStore, bank, (_, _) => posts++, state.Ledger, _ => wakes++);
        Check(posts == 1 && wakes == 1 && state.Ledger.ListAll().Count == 1
            && state.SnapshotStore.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == plan.Snapshot.Envelope.NextLineageToken
            && !Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Any()
            && (consumed is null || consumed == JsonSerializer.Serialize(state.Ledger.ListAll())), "recovery incomplete or overwrote consumed support");
        if (phase == "after-snapshot") PendingBankTestDebt.VerifyAncillary(bank, "after");
    }

    private static void RecoveryConflict(Func<string, SaveSnapshotPackage> fixture, string root, string conflict)
    {
        var state = Open(root); var plan = Plan(fixture, root, state);
        try { state.SnapshotCommits.CommitSupport(new FaultStore((IColonySnapshotPackageStore)state.SnapshotStore, "before-snapshot"),
            state.BankLoans, state.Ledger, plan.Package, plan.Snapshot, plan.Mutation, (_, _) => { }, _ => { }); }
        catch (IOException) { }
        var support = plan.Mutation.Support;
        if (conflict == "newer-snapshot") ((IColonySnapshotPackageStore)state.SnapshotStore).StoreLatest(fixture("newer"), plan.Package.Index, DateTimeOffset.UtcNow);
        if (conflict == "same-id-payload") state.Ledger.Append(support with { Payload = ((SupportPawnEventPayload)support.Payload) with { PawnName = "other" } });
        if (conflict == "same-key-id") state.Ledger.Append(support with { EventId = "other" });
        if (conflict == "same-id-key") state.Ledger.Append(support with { IdempotencyKey = "other" });
        if (conflict.StartsWith("journal-"))
        {
            string path = Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Single();
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            if (conflict == "journal-owner") node["Support"]!["Support"]!["Actor"]!["UserId"] = "other";
            if (conflict == "journal-version") node["Version"] = 11;
            if (conflict == "journal-mixed") node["Gift"] = node["Support"]!["Support"]!.DeepClone();
            if (conflict == "journal-status") node["Support"]!["Support"]!["Status"] = (int)ServerEventStatus.AppliedToSnapshot;
            File.WriteAllText(path, node.ToJsonString());
        }
        string before = State(state); bool rejected = false;
        try { var reopened = Open(root); reopened.SnapshotCommits.Recover(reopened.SnapshotStore, reopened.BankLoans, (_, _) => { }, reopened.Ledger); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && before == State(state) && Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Length == 1,
            "recovery overwrote conflicting evidence: " + conflict);
    }

    private static async Task EndpointStorageFailure(Func<string, SaveSnapshotPackage> fixture, string root)
    {
        var state = Open(root); var package = Prepare(fixture, state, root); var request = Request(package);
        Sql(root, "create trigger fail_support before insert on events begin select raise(abort, 'support fault'); end;");
        bool failed = false; try { await Invoke(state, request, package.Payload); } catch (SqliteException) { failed = true; }
        Check(failed && state.Ledger.ListAll().Count == 0 && Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Length == 1,
            "endpoint did not journal the failed support append");
        Sql(root, "drop trigger fail_support"); state = Open(root);
        var replay = await Invoke(state, request, package.Payload);
        Check(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && state.Ledger.ListAll().Count == 1,
            "endpoint restart lost support");
        string before = State(state); state = Open(root);
        Check((await Invoke(state, request, package.Payload)).Result.Accepted && before == State(state), "second restart duplicated support");
    }

    private static async Task Concurrent(Func<string, SaveSnapshotPackage> fixture, string root, bool sameKey)
    {
        var state = Open(root); var package = Prepare(fixture, state, root);
        var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, now, out string session);
        string token = state.AuthTokens.Issue(Actor.UserId, Actor.UserId, Actor.ColonyId!, session, now);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateSupportPawnWithSnapshot", state,
                _ => Request(package, sameKey ? "same" : "different-" + i), package.Payload, Actor, token))));
        Check(state.Ledger.ListAll().Count == 1 && results.Count(r => r.Result.Accepted) == (sameKey ? 12 : 1)
            && results.Count(r => r.Result.Accepted && r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1,
            "concurrent support duplicated a pawn or rejected identical requests: " + sameKey + " " + JsonSerializer.Serialize(results));
    }

    private static void RevokedWhileWaiting(Func<string, SaveSnapshotPackage> fixture, string root, bool replaced)
    {
        var state = Open(root); var package = Prepare(fixture, state, root);
        var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, now, out string session);
        string token = state.AuthTokens.Issue(Actor.UserId, Actor.UserId, Actor.ColonyId!, session, now);
        string before = State(state);
        SnapshotMutationAuthorizationProbe.RevokedWhileWaiting(state, Actor.UserId, Actor.ColonyId!, replaced,
            () => SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateSupportPawnWithSnapshot", state,
                _ => Request(package), package.Payload, Actor, token).GetAwaiter().GetResult().Result.Accepted);
        Check(before == State(state) && !Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Any(),
            "revoked session published a snapshot or intent");
    }

    private static void Migration(Func<string, SaveSnapshotPackage> fixture, string root)
    {
        string database = Path.Combine(root, "events.sqlite");
        ServerDatabaseMigrator.Migrate(database);
        var state = Open(root); var plan = Plan(fixture, root, state);
        state.Ledger.Append(plan.Mutation.Support with { Payload = ((SupportPawnEventPayload)plan.Mutation.Support.Payload) with { CreationRequestHash = null } });
        string before = JsonSerializer.Serialize(state.Ledger.ListAll());
        Sql(root, "update server_schema_metadata set metadata_value='26' where metadata_key='schema_version'");
        Check(ServerDatabaseMigrator.Assess(database).Status == ServerPersistenceMigrationStatus.SafeMigrationAvailable, "v26 not migratable");
        ServerDatabaseMigrator.Migrate(database);
        Check(ServerDatabaseMigrator.ReadVersion(database) == ServerDatabaseSchema.CurrentVersion && before == JsonSerializer.Serialize(Open(root).Ledger.ListAll()), "migration rewrote historical support");
    }

    private static CreateSupportPawnWithSnapshotRequest Change<T>(CreateSupportPawnWithSnapshotRequest request, string path, T value)
    {
        var node = JsonSerializer.SerializeToNode(request)!; var parent = node;
        var parts = path.Split('.'); foreach (string part in parts[..^1]) parent = parent[part]!;
        parent[parts[^1]] = JsonSerializer.SerializeToNode(value);
        return node.Deserialize<CreateSupportPawnWithSnapshotRequest>()!;
    }
    private static string State(ClashOfRimNetworkState state) => JsonSerializer.Serialize(state.Ledger.ListAll()) + JsonSerializer.Serialize(state.SnapshotStore.ListLatest());
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Sql(string root, string sql) { using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-support-commit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private sealed class FaultStore(IColonySnapshotPackageStore inner, string phase) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (phase == "before-snapshot") throw new IOException("before snapshot");
            inner.StoreLatest(package, index, at);
            if (phase == "after-snapshot") throw new IOException("after snapshot");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
