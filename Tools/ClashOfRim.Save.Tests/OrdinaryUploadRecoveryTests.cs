using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class OrdinaryUploadRecoveryTests
{
    private static readonly SnapshotAchievementCandidateDto Candidate = new("ordinary-upload", "once", 7, aggregationKind: AchievementRegistry.AggregationSum);

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool relocation in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string failure in new[] { "before", "after", "bank", "players", "achievement", "notification", "processor", "schedule", "ack" })
            WithFixture(fixture, relocation, f => VerifyRecovery(f, reopen, failure));
        foreach (bool reopen in new[] { false, true })
        foreach (string conflict in new[] { "newer", "parent-token", "parent-hash", "published-hash" })
            WithFixture(fixture, false, f => VerifyConflict(f, reopen, conflict));
        WithFixture(fixture, false, VerifyBackground);
        WithFixture(fixture, false, VerifyJournalFailure);
        WithFixture(fixture, false, VerifyRejectedAndNested);
        WithFixture(fixture, false, VerifyInitialWithoutPendingBusiness);
        WithFixture(fixture, false, VerifyAuthorizationAfterPreparation);
        WithFixture(fixture, false, VerifyCommittedInput);
        foreach (string corrupt in new[] { "missing-context", "foreign-parent", "missing-operations", "null-candidates" })
            WithFixture(fixture, false, f => VerifyBadJournal(f, corrupt));
    }

    private static void VerifyRecovery(Fixture f, bool reopen, string failure)
    {
        f.Fail(failure);
        ExpectFailure(() => f.Upload());
        var intent = f.ReadIntent();
        Require(intent.Version == 10 && intent.Upload is not null && intent.Upload.Previous == f.Before.Envelope
            && intent.Upload.ConfirmationOperation == f.Operation && intent.Upload.AchievementCandidates.Single().AchievementId == Candidate.AchievementId
            && intent.PendingOperations!.Debts.Count == 1, "journal lost original upload context or confirmation batch");
        Require(f.State.SnapshotCommits.HasPendingUploads, "pending upload disappeared after failure");
        f.ClearFailure();
        f.AddDebt("later");
        if (reopen) f.Open();
        // A retry cannot replace the accepted operation or introduce new candidates.
        var response = f.Upload(operation: "changed", candidates: [new("changed", "new", 99)]);
        Require(response.Result.Accepted && response.NextLineageToken == intent.Package.Envelope.NextLineageToken,
            "retry did not acknowledge the original accepted token");
        VerifyCompleted(f, intent);
        Require(f.State.BankLoans.FindDebtByIdempotencyKey("later")!.Status == BankLoanRegistry.StatusPendingActivation,
            "recovery recaptured work created after acceptance");
        var context = f.Contexts.Last();
        Require(ReferenceEquals(context, f.Deferred.Context), "inline and deferred plugins received different upload contexts");
        Require(context.ExtraData.ConfirmationOperation == f.Operation && context.OccurredAtUtc == intent.AcceptedAtUtc
            && context.ExtraData.SnapshotUploadKind == intent.SnapshotUploadKind
            && context.RegisterPlayerColonySite == !f.Relocation && context.ExtraData.AchievementCandidates!.Single().AchievementId == Candidate.AchievementId,
            "post-processing used retry values instead of the original context");
        int calls = f.Contexts.Count;
        var again = f.Upload();
        Require(again.Result.Accepted && again.NextLineageToken == response.NextLineageToken && f.Contexts.Count == calls,
            "completed duplicate rotated the token or reran post-processing");
        var changed = f.Package with { Envelope = f.Package.Envelope with { Identity = f.Package.Envelope.Identity with { SnapshotId = "different-id" } } };
        Require(!f.Upload(package: changed).Result.Accepted, "old lineage was accepted under a new identity");
        Require(!f.Upload(package: f.Before with { Envelope = f.Before.Envelope with { Identity = f.Package.Envelope.Identity } }).Result.Accepted,
            "same snapshot identity accepted different content");
    }

    private static void VerifyCompleted(Fixture f, SnapshotCommitIntent intent)
    {
        Require(!f.State.SnapshotCommits.HasPendingUploads && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "completed journal was not removed");
        Require(SnapshotCommitService.SameVersion(f.Files.GetLatest("userA", "colonyA")!.Envelope, intent.Package.Envelope)
            && f.State.Players.FindByUserId("userA")!.CurrentSnapshotId == "ordinary", "snapshot/player projection was not recovered");
        Require(f.State.BankLoans.FindDebtByIdempotencyKey("original") is { Status: BankLoanRegistry.StatusActive, SnapshotId: "ordinary" },
            "original pending business confirmation was lost");
        Require(f.State.Achievements.ListAggregates().Single(item => item.AchievementId == Candidate.AchievementId).Value == 7
            && f.State.Achievements.ListAggregates().All(item => item.AchievementId != "changed"), "candidate was lost, repeated or replaced");
        Require(f.State.SnapshotPostUploadJobs.Find("ordinary:test.ordinary-deferred") is not null || Volatile.Read(ref f.Deferred.Completed) > 0,
            "deferred work was not scheduled before removing the intent");
        Require(f.State.WorldConfiguration.Current!.PlayerColonySites.Any(site => site.UserId == "userA") == !f.Relocation,
            "relocation registration policy changed during recovery");
    }

    private static void VerifyCommittedInput(Fixture f)
    {
        var snapshot = f.Files.GetLatest("userA", "colonyA")!;
        f.OnProcess = () => Require(f.State.Players.FindByUserId("userA")?.CurrentSnapshotId == snapshot.Identity.SnapshotId,
            "plugin notification ran before the core snapshot reference projection");
        f.State.SnapshotPostUploads.AfterCommit(snapshot, "custom-upload-kind");
        var context = f.Contexts.Single();
        Require(ReferenceEquals(context, f.Deferred.Context) && ReferenceEquals(context.Snapshot, snapshot)
            && context.Kind == SnapshotPostUploadKind.AuthoritativeColonySnapshot
            && context.UserId == "userA" && context.ColonyId == "colonyA" && context.SessionId is null
            && context.OccurredAtUtc == snapshot.AcceptedAtUtc && context.RegisterPlayerColonySite
            && context.ExtraData.SnapshotUploadKind == "custom-upload-kind" && context.ExtraData.ConfirmationOperation is null
            && context.ExtraData.AchievementCandidates is null, "committed entry changed the original snapshot context");
    }

    private static void VerifyConflict(Fixture f, bool reopen, string conflict)
    {
        f.Fail(conflict == "published-hash" ? "after" : "before"); ExpectFailure(() => f.Upload()); f.ClearFailure();
        var current = f.Files.GetLatestPackage("userA", "colonyA")!;
        var envelope = conflict switch
        {
            "newer" => current.Envelope with { Identity = current.Envelope.Identity with { SnapshotId = "newer" } },
            "parent-token" => current.Envelope with { NextLineageToken = "changed" },
            _ => current.Envelope with { PayloadSha256 = "changed" }
        };
        f.Files.StoreLatest(current with { Envelope = envelope }, current.Index, DateTimeOffset.UtcNow);
        if (reopen) f.Open();
        ExpectFailure(() => ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State));
        Require(f.Files.GetLatest("userA", "colonyA")!.Envelope == envelope && f.State.SnapshotCommits.HasPendingUploads
            && f.State.BankLoans.FindDebtByIdempotencyKey("original")!.Status == BankLoanRegistry.StatusPendingActivation,
            "recovery overwrote conflicting state or applied its pending business records");
    }

    private static void VerifyBackground(Fixture f)
    {
        f.Fail("processor"); ExpectFailure(() => f.Upload()); var intent = f.ReadIntent(); f.ClearFailure(); f.Open();
        using var background = new SnapshotPostUploadBackgroundService(f.State, new SnapshotPostUploadProcessorSource(() => [f.Deferred]));
        background.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Require(SpinWait.SpinUntil(() => !f.State.SnapshotCommits.HasPendingUploads, TimeSpan.FromSeconds(10)), "background did not recover ordinary upload");
            VerifyCompleted(f, intent);
        }
        finally { background.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void VerifyJournalFailure(Fixture f)
    {
        Directory.Delete(f.Journal); File.WriteAllText(f.Journal, "unavailable");
        ExpectFailure(() => f.Upload());
        Require(f.Files.GetLatest("userA", "colonyA")!.Envelope == f.Before.Envelope
            && f.State.BankLoans.FindDebtByIdempotencyKey("original")!.Status == BankLoanRegistry.StatusPendingActivation,
            "snapshot or business state changed before intent persistence");
    }

    private static void VerifyInitialWithoutPendingBusiness(Fixture f)
    {
        f.State.BankLoans.RemoveForColony("userA", "colonyA");
        f.Files.RemoveLatest("userA", "colonyA");
        var initial = f.Before with { Envelope = f.Before.Envelope with { Identity = f.Package.Envelope.Identity } };
        f.Fail("processor"); var failure = ExpectFailure(() => f.Upload(package: initial));
        Require(f.State.SnapshotCommits.HasPendingUploads, "initial upload failed before journal: " + failure);
        var intent = f.ReadIntent();
        Require(intent.Upload!.Previous is null && intent.PendingOperations!.IsEmpty,
            "initial upload without ancillary business did not retain its context");
        f.ClearFailure(); f.Open();
        var response = f.Upload(package: initial);
        Require(response.Result.Accepted && response.NextLineageToken == intent.Package.Envelope.NextLineageToken
            && !f.State.SnapshotCommits.HasPendingUploads && f.State.Achievements.ListAggregates().Single(item => item.AchievementId == Candidate.AchievementId).Value == 7,
            "initial ordinary upload was not recovered idempotently");
    }

    private static void VerifyAuthorizationAfterPreparation(Fixture f)
    {
        bool prepared = false;
        f.OnPrepare = () => { prepared = true; f.State.AuthTokens.RevokeForColony("userA", "colonyA"); };
        var response = f.Upload();
        Require(prepared && !response.Result.Accepted && !f.State.SnapshotCommits.HasPendingUploads
            && f.Files.GetLatest("userA", "colonyA")!.Envelope == f.Before.Envelope,
            "upload ignored authorization revoked after its initial check");
    }

    private static void VerifyRejectedAndNested(Fixture f)
    {
        Require(!f.Upload(package: f.Package with { Payload = [1] }).Result.Accepted
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "invalid payload produced a commit intent");
        f.OnProcess = () =>
        {
            try
            {
                typeof(ClashOfRimNetworkServer).GetMethod("ReceiveSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object?[] { f.State, "userA", "colonyA", "ordinary", ProtocolDtoMapper.ToMetadataDto(f.Package), f.Package.Payload,
                        DateTimeOffset.UtcNow, true, true, null, null, null, false });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { throw new IOException("nested publication rejected"); }
            throw new Exception("nested publication was not rejected");
        };
        ExpectFailure(() => f.Upload()); var intent = f.ReadIntent(); f.OnProcess = null;
        Require(SnapshotCommitService.SameVersion(f.Files.GetLatest("userA", "colonyA")!.Envelope, intent.Package.Envelope), "nested publication changed the accepted token");
        ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State); VerifyCompleted(f, intent);
    }

    private static void VerifyBadJournal(Fixture f, string corrupt)
    {
        f.Fail("before"); ExpectFailure(() => f.Upload()); f.ClearFailure();
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        switch (corrupt)
        {
            case "missing-context": json["Upload"] = null; break;
            case "foreign-parent": json["Upload"]!["Previous"]!["Identity"]!["OwnerId"] = "other"; break;
            case "missing-operations": json["PendingOperations"] = null; break;
            default: json["Upload"]!["AchievementCandidates"] = null; break;
        }
        File.WriteAllText(path, json.ToJsonString());
        bool rejected = false;
        try { _ = new SnapshotCommitService(f.Journal); } catch (InvalidDataException) { rejected = true; }
        Require(rejected && f.Files.GetLatest("userA", "colonyA")!.Envelope == f.Before.Envelope, "bad upload journal was accepted");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root, Database, Journal;
        public readonly bool Relocation;
        public string? Operation => Relocation ? SnapshotConfirmationOperations.ColonyRelocation : null;
        public readonly SaveSnapshotPackage Before, Package;
        public FileColonySnapshotIndexStore Files = null!;
        public ClashOfRimNetworkState State = null!;
        public readonly List<SnapshotPostUploadContext> Contexts = [];
        public readonly DeferredProcessor Deferred = new();
        public Action? OnProcess, OnPrepare;
        private CrashStore store = null!;
        private string? failure;
        private FileStream? heldIntent;

        public Fixture(string root, bool relocation, SaveSnapshotPackage package)
        {
            Root = root; Relocation = relocation; Database = Path.Combine(root, "server.sqlite"); Journal = Path.Combine(root, "intents");
            ServerDatabaseMigrator.Migrate(Database); Open();
            var accepted = new SnapshotUploadReceiver(Files).Receive(new SnapshotUploadContext("userA", "colonyA", package.Envelope.Identity.SnapshotId!),
                package, DateTimeOffset.UtcNow).AcceptedSnapshot!;
            Before = Files.GetLatestPackage("userA", "colonyA")!;
            using var compressed = new MemoryStream(package.Payload); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            var document = XDocument.Load(gzip);
            document.Root!.Element("game")!.Add(new XElement("components", new XElement("li",
                new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
                new XElement("clashOfRimLineageSnapshotId", Before.Envelope.Identity.SnapshotId),
                new XElement("clashOfRimLineageToken", accepted.Envelope.NextLineageToken))));
            string path = Path.Combine(root, "child.rws"); document.Save(path);
            Package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity("userA", "colonyA", "ordinary"), DateTimeOffset.UtcNow);
            ColonyDeletionStageTests.SeedWorld(State.WorldConfiguration); AddDebt("original");
        }
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(Root, "snapshots")); store = new CrashStore(Files);
            var processor = new InlineProcessor(context =>
                {
                    Contexts.Add(context); OnProcess?.Invoke();
                    if (failure == "processor") throw new IOException("injected processor failure");
                    if (failure == "ack") heldIntent = new FileStream(Directory.EnumerateFiles(Journal, "*.json").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
                });
            State = new ClashOfRimNetworkState(snapshotStore: store, snapshotCommits: new SnapshotCommitService(Journal),
                ledger: new SqliteAuthoritativeEventLedger(Database), playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.BankLoans), null),
                achievements: new AchievementRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.Achievements), null),
                snapshotPostUploadJobs: new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(Database)),
                worldConfigurationRegistry: new WorldConfigurationRegistry(new SqliteKeyedJsonRecordStore(Database, "world-configuration"), null,
                    new SqliteJsonPersistenceSlot(Database, "world-configuration"), null),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.ordinary", "ordinary", "1", "test", "", [])
                    { SnapshotPostUploadProcessors = [processor, Deferred], SaveIndexExtensions = [new PreparationHook(() => OnPrepare?.Invoke())] }]));
        }
        public void AddDebt(string key) => State.BankLoans.CreateDebt(key, "userA", "colonyA", "before", 50,
            BankLoanRegistry.DebtSourceFine, "fine", "source", 1000, DateTimeOffset.UtcNow, true);
        public UploadSnapshotResponse Upload(string? operation = null, IReadOnlyList<SnapshotAchievementCandidateDto>? candidates = null, SaveSnapshotPackage? package = null)
        {
            package ??= Package;
            return SnapshotEndpointTestClient.Invoke<UploadSnapshotResponse>("UploadSnapshot", State,
                token => new UploadSnapshotMetadataRequest("ordinary-request", "userA", "colonyA", package.Envelope.Identity.SnapshotId!,
                    ProtocolDtoMapper.ToMetadataDto(package), token, operation ?? Operation, candidates ?? [Candidate]),
                package.Payload, new ProtocolIdentity("userA", "colonyA", null)).GetAwaiter().GetResult();
        }
        public SnapshotCommitIntent ReadIntent()
        {
            var paths = Directory.EnumerateFiles(Journal, "*.json").ToArray();
            Require(paths.Length == 1, "failed upload did not retain one complete recovery intent");
            return JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(paths[0]))!;
        }
        public void Fail(string fault)
        {
            failure = fault; store.Fault = fault;
            string? table = fault switch { "bank" => "server_bank_debts", "players" => "server_players", "achievement" => "server_achievement_events",
                "notification" => "events", "schedule" => "server_snapshot_post_upload_jobs", _ => null };
            if (table is not null) Sql(Database, $"create trigger fail_ordinary before insert on {table} begin select raise(abort, 'injected ordinary upload failure'); end;");
        }
        public void ClearFailure() { failure = null; store.Fault = null; heldIntent?.Dispose(); heldIntent = null; Sql(Database, "drop trigger if exists fail_ordinary;"); }
        public void Dispose() => heldIntent?.Dispose();
    }
    private sealed class CrashStore(FileColonySnapshotIndexStore inner) : IColonySnapshotPackageStore, IColonySnapshotHistoryStore
    {
        public string? Fault;
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        { if (Fault == "before") throw new IOException("before publication"); inner.StoreLatest(package, index, at); if (Fault == "after") throw new IOException("after publication"); }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string user, string colony) => inner.GetLatest(user, colony);
        public SaveSnapshotPackage? GetLatestPackage(string user, string colony) => inner.GetLatestPackage(user, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string user, string colony) => inner.RemoveLatest(user, colony);
        public bool HasAcceptedOriginalHash(string user, string colony, string hash) => inner.HasAcceptedOriginalHash(user, colony, hash);
    }
    private sealed class DeferredProcessor : IDeferredSnapshotPostUploadProcessor
    {
        public int Completed;
        public SnapshotPostUploadContext? Context;
        public string Id => "test.ordinary-deferred";
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.Notification;
        public int Order => 200;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Deferred;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public string CapturePayload(SnapshotPostUploadContext context) { Context = context; return JsonSerializer.Serialize(context.ExtraData); }
        public void ProcessDeferred(SnapshotPostUploadDeferredContext context) => Interlocked.Increment(ref Completed);
    }
    private sealed class InlineProcessor(Action<SnapshotPostUploadContext> action) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.ordinary-context";
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.Notification;
        public int Order => 100;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) => action(context);
    }
    private sealed class PreparationHook(Action action) : ISaveIndexExtension
    {
        public IEnumerable<ThingSummary> ReadContainedThings(XElement containerThing, ThingSummary container, SaveIndexReadContext context)
        { action(); return []; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Ordinary upload: " + message); }
    private static Exception ExpectFailure(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or InvalidOperationException) { return ex; } throw new Exception("Ordinary upload: fault not reached"); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithFixture(Func<string, SaveSnapshotPackage> fixture, bool relocation, Action<Fixture> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-ordinary-upload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { using var f = new Fixture(root, relocation, fixture("before")); action(f); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
