using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class ConfirmationJournalTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool single in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "bank", "players", "processor", "plan", "ledger", "after-ledger", "achievement", "notification", "receipt", "ack" })
            WithFixture(fixture, single, f => Recovery(f, reopen, fault));
        foreach (bool reopen in new[] { false, true })
        foreach (string conflict in new[] { "newer", "token", "hash", "owner", "payload", "planned-result", "missing-created" })
            WithFixture(fixture, false, f => Conflict(f, reopen, conflict));
        WithFixture(fixture, false, Background);
        WithFixture(fixture, false, InvalidAndChangedRequests);
        WithFixture(fixture, false, AfterPreparationRevocation);
        foreach (bool reopen in new[] { false, true })
        {
            WithFixture(fixture, false, f => SupportDeathOrdering(f, reopen));
            WithFixture(fixture, false, f => ExistingSnapshotIdentity(f, reopen));
            foreach (string kind in new[] { "gift", "support", "return", "loss", "drop-pod" })
                WithFixture(fixture, true, f => RemainingKinds(f, kind, reopen));
        }
        WithFixture(fixture, false, EmptyAndUnwritableJournal);
        foreach (string corrupt in new[] { "version", "sources", "results", "changes", "receipt", "intent-name", "receipt-name" })
            WithFixture(fixture, false, f => Corrupt(f, corrupt));
        WithFixture(fixture, false, InterruptedTemporaryFiles);
        foreach (string storage in new[] { "memory", "file", "sqlite" }) BatchSemantics(storage);
    }

    private static void Recovery(Fixture f, bool reopen, string fault)
    {
        f.OnPost = () =>
        {
            Require(f.State.SnapshotCommits.IsApplying && f.State.SnapshotCommits.HasPendingUploads
                && Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate)
                && Monitor.IsEntered(f.State.BankLoans.SnapshotCommitGate)
                && Monitor.IsEntered(f.State.MercenaryContracts.SnapshotCommitGate), "post-processing escaped its coordinator scope");
            f.State.SnapshotCommits.Recover(f.Files, f.State.BankLoans, (_, _) => throw new Exception("Nested replay applied the active intent."));
        };
        f.Fail(fault); ExpectFailure(() => f.Confirm());
        Require(!f.State.SnapshotCommits.IsApplying && f.State.SnapshotCommits.HasPending, "failed application lost its pending state or leaked its applying scope");
        var intent = f.Intent();
        Require(intent.Version == 11 && intent.Upload?.Confirmation is { } mutation && mutation.Request.Single == f.Single
            && mutation.Request.BaseSnapshotId == "before", "missing original confirmation intent");
        bool planned = fault is "ledger" or "after-ledger" or "achievement" or "notification" or "receipt" or "ack";
        Require((intent.Upload!.Confirmation!.Plan is not null) == planned, "plan checkpoint has incorrect phase");
        if (fault is "plan" or "ledger")
            Require(f.State.Ledger.Find("trade")!.Status != ServerEventStatus.AppliedToSnapshot
                && f.State.Ledger.Find("gift")!.Status != ServerEventStatus.AppliedToSnapshot,
                "failed plan/ledger stage partially applied confirmations");
        f.Clear();
        if (reopen) f.Open();
        int calls = f.PostCalls;
        var response = f.Confirm();
        Require(response.Result.Accepted && response.NextLineageToken == intent.Package.Envelope.NextLineageToken,
            "retry did not return original lineage");
        VerifyCompleted(f, intent, response);
        if (planned) Require(f.PostCalls == calls, "checkpointed post-processing reran after plan persistence");
        var ledger = JsonSerializer.Serialize(f.State.Ledger.ListAll());
        var repeated = f.Confirm();
        Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(repeated) && ledger == JsonSerializer.Serialize(f.State.Ledger.ListAll()),
            "completed retry changed results or events");
        f.Open();
        Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(f.Confirm()), "receipt did not survive restart");
    }

    private static void VerifyCompleted(Fixture f, SnapshotCommitIntent intent, ConfirmEventApplicationsResponse response)
    {
        Require(!f.State.SnapshotCommits.IsApplying && !f.State.SnapshotCommits.HasPending
            && !f.State.SnapshotCommits.HasPendingUploads && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "completed intent remained");
        Require(SnapshotCommitService.SameVersion(f.Files.GetLatest("userA", "colonyA")!.Envelope, intent.Package.Envelope), "published snapshot changed");
        var applied = f.State.Ledger.Find("trade")!;
        Require(applied.Status == ServerEventStatus.AppliedToSnapshot && applied.AppliedAtUtc == intent.AcceptedAtUtc
            && applied.AppliedSnapshotId == "confirmed" && applied.LastApplicationResult == EventApplicationResultKind.Applied,
            "trade was not applied with original attribution");
        Require(f.State.Achievements.ListAggregates().Count(item => item.AchievementId == "trades_10" && item.Value == 10) == 2,
            "trade achievements were lost or repeated");
        Require(f.State.BankLoans.FindDebtByIdempotencyKey("pending")!.Status == BankLoanRegistry.StatusActive
            && f.State.Players.FindByUserId("userA")!.CurrentSnapshotId == "confirmed", "business/player projection not recovered");
        Require(Directory.EnumerateFiles(Path.Combine(f.Journal, "confirmation-receipts"), "*.json").Count() == 1, "receipt not durable");
        Require(response.Applications.Count == f.Entries.Count, "entry count changed");
        if (!f.Single)
        {
            Require(response.Applications.Take(3).All(item => item.Result.Accepted)
                && response.Applications.Skip(3).Take(4).All(item => !item.Result.Accepted)
                && response.Applications.Last().ServerValidationResult == "AlreadyApplied", "mixed batch results/order changed");
            Require(f.State.Ledger.Find("reject")!.LastApplicationResult == EventApplicationResultKind.NeedsManualReview
                && f.State.Ledger.Find("cancelled")!.Status == ServerEventStatus.Cancelled
                && f.State.Ledger.Find("foreign")!.LastApplicationResult == EventApplicationResultKind.ReadyToApply,
                "rejected entries were not recorded safely");
        }
    }

    private static void Conflict(Fixture f, bool reopen, string conflict)
    {
        f.Fail(conflict == "planned-result" ? "ledger" : "before"); ExpectFailure(() => f.Confirm()); f.Clear();
        if (conflict is "newer" or "token" or "hash")
        {
            var current = f.Files.GetLatestPackage("userA", "colonyA")!;
            var envelope = conflict == "newer" ? current.Envelope with { Identity = current.Envelope.Identity with { SnapshotId = "newer" } }
                : conflict == "token" ? current.Envelope with { NextLineageToken = "changed" } : current.Envelope with { PayloadSha256 = "changed" };
            f.Files.StoreLatest(current with { Envelope = envelope }, current.Index, DateTimeOffset.UtcNow);
        }
        else if (conflict == "owner") Sql(f.Database, "update events set actor_user_id = 'intruder' where event_id = 'trade';");
        else if (conflict == "payload") Sql(f.Database, "update events set payload_json = json_set(payload_json, '$.TradeId', 'changed') where event_id = 'trade';");
        else if (conflict == "planned-result") f.State.Ledger.ReportApplicationResult("trade", EventApplicationResultKind.NeedsManualReview, "changed", null);
        else f.State.Ledger.Append(Fixture.Gift("missing"));
        if (reopen) f.Open();
        string before = JsonSerializer.Serialize(f.State.Ledger.ListAll());
        var envelopeBefore = f.Files.GetLatest("userA", "colonyA")!.Envelope;
        ExpectFailure(() => f.State.SnapshotMutations.Recover());
        Require(JsonSerializer.Serialize(f.State.Ledger.ListAll()) == before && f.Files.GetLatest("userA", "colonyA")!.Envelope == envelopeBefore
            && f.State.SnapshotCommits.HasPendingUploads, "conflict overwrote newer state or discarded intent");
    }

    private static void Background(Fixture f)
    {
        f.Fail("ledger"); ExpectFailure(() => f.Confirm()); var intent = f.Intent(); f.Clear(); f.Open();
        using var service = new SnapshotPostUploadBackgroundService(f.State.SnapshotPostUploadBackground);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Require(SpinWait.SpinUntil(() => !f.State.SnapshotCommits.HasPendingUploads, TimeSpan.FromSeconds(10)), "background did not recover confirmation");
            VerifyCompleted(f, intent, f.Confirm());
        }
        finally { service.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void InvalidAndChangedRequests(Fixture f)
    {
        var before = f.Files.GetLatest("userA", "colonyA")!.Envelope;
        Require(!f.Confirm(entries: [new("", null, "")]).Result.Accepted && !f.State.SnapshotCommits.HasPendingUploads
            && f.Files.GetLatest("userA", "colonyA")!.Envelope == before, "invalid entry published a snapshot");
        f.Fail("after"); ExpectFailure(() => f.Confirm()); var intent = f.Intent(); f.Clear();
        Require(!f.Confirm(entries: [new("foreign", null, "ItemDeliveryAnchored")]).Result.Accepted,
            "retry replaced original confirmation entries");
        VerifyCompleted(f, intent, f.Confirm());
        Require(!f.Confirm(baseId: "other").Result.Accepted, "retry replaced original base identity");
        var different = f.Before with { Envelope = f.Before.Envelope with { Identity = f.Package.Envelope.Identity } };
        Require(!f.Confirm(package: different).Result.Accepted, "receipt accepted different snapshot content");
        // Returning an old receipt after later publication must not roll back the latest snapshot.
        var newer = f.Files.GetLatestPackage("userA", "colonyA")!;
        var newerEnvelope = newer.Envelope with { Identity = newer.Envelope.Identity with { SnapshotId = "newer" } };
        f.Files.StoreLatest(newer with { Envelope = newerEnvelope }, newer.Index, DateTimeOffset.UtcNow);
        Require(f.Confirm().Result.Accepted && f.Files.GetLatest("userA", "colonyA")!.Envelope == newerEnvelope,
            "old receipt overwrote a later snapshot");
    }

    private static void AfterPreparationRevocation(Fixture f)
    {
        f.OnPrepare = () => f.State.AuthTokens.RevokeForColony("userA", "colonyA");
        var response = f.Confirm();
        Require(!response.Result.Accepted && !f.State.SnapshotCommits.HasPendingUploads
            && f.Files.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "revoked auth published confirmation");
    }

    private static void SupportDeathOrdering(Fixture f, bool reopen)
    {
        string path = Path.Combine(f.Root, "child.rws");
        var doc = XDocument.Load(path);
        doc.Root!.Element("game")!.Element("maps")!.Element("li")!.Element("things")!.Add(
            new XElement("thing", new XAttribute("Class", "Pawn"), new XElement("id", "absent"), new XElement("def", "Human"),
                new XElement("kindDef", "Colonist"), new XElement("healthTracker", new XElement("healthState", "Dead"))));
        doc.Save(path);
        var package = SaveSnapshotPackageBuilder.FromFile(path, f.Package.Envelope.Identity, DateTimeOffset.UtcNow);
        f.Fail("processor"); ExpectFailure(() => f.Confirm(package: package)); f.Clear();
        Require(f.State.Ledger.Find("support")!.Status == ServerEventStatus.Failed, "snapshot did not process support death before interruption");
        if (reopen) f.Open();
        var response = f.Confirm(package: package);
        Require(!response.Applications.Single(item => item.EventId == "support").Result.Accepted
            && f.State.Ledger.Find("support")!.Status == ServerEventStatus.Failed
            && f.State.Ledger.FindByIdempotencyKey(SupportClosureKeys.Loss("support", "SnapshotDeath")) is not null,
            "confirmation undid support death or lost its return event");
    }

    private static void RemainingKinds(Fixture f, string kind, bool reopen)
    {
        if (kind == "return") f.State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, new("other", "other-colony"), new("userA", "colonyA"),
            kind, false, new SupportPawnEventPayload("owner:other/colony:other-colony/pawn:absent", "source", "Ally", true, null, ReturnToSender: true), DateTimeOffset.UtcNow) with { EventId = kind });
        if (kind == "loss") f.State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Raid, new("other", "other-colony"), new("userA", "colonyA"),
            kind, false, new RaidEventPayload("defender", null, null, null, null,
                AttackerLoss: new RaidAttackerLossRecord("source", "before", ["pawn:absent"], [], "lost")), DateTimeOffset.UtcNow) with
                { EventId = kind, DeliveredToSnapshotId = "before", DeliveredAtUtc = DateTimeOffset.UnixEpoch });
        if (kind == "drop-pod")
        {
            var trade = f.State.Ledger.Find("trade")!;
            f.State.Ledger.Append(trade with { EventId = kind, IdempotencyKey = kind,
                Payload = ((TradeEventPayload)trade.Payload) with { Stage = TradeStage.ServerDropPodExchange } });
        }
        ConfirmEventApplicationEntry[] entries = [new(kind, "source", kind == "return" ? "SupportPawnReturnCaravanCreated"
            : kind == "support" ? "SupportPawnArrived" : "ItemDeliveryAnchored")];
        f.Fail("after"); ExpectFailure(() => f.Confirm(entries: entries)); var intent = f.Intent(); f.Clear();
        if (reopen) f.Open();
        var result = f.Confirm(entries: entries);
        Require(result.Result.Accepted && f.State.Ledger.Find(kind) is { Status: ServerEventStatus.AppliedToSnapshot } applied
            && applied.AppliedAtUtc == intent.AcceptedAtUtc, kind + " confirmation did not recover");
        Require(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(f.Confirm(entries: entries)), kind + " retry changed receipt");
    }

    private static void EmptyAndUnwritableJournal(Fixture f)
    {
        Directory.Delete(f.Journal); File.WriteAllText(f.Journal, "blocked");
        ExpectFailure(() => f.Confirm());
        Require(f.Files.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before" && !f.State.SnapshotCommits.HasPendingUploads,
            "failed intent write published a snapshot");
        File.Delete(f.Journal); Directory.CreateDirectory(f.Journal);
        var response = f.Confirm(entries: []);
        Require(response.Result.Accepted && response.Applications.Count == 0 && f.State.Ledger.Find("trade")!.Status != ServerEventStatus.AppliedToSnapshot,
            "empty batch applied unrelated events");
        f.Open();
        Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(f.Confirm(entries: [])), "empty batch receipt was lost");
    }

    private static void ExistingSnapshotIdentity(Fixture f, bool reopen)
    {
        // Legacy callers may retain the latest snapshot ID while advancing its validated lineage/content.
        var package = f.Package with { Envelope = f.Package.Envelope with { Identity = f.Before.Envelope.Identity } };
        f.Fail("after"); ExpectFailure(() => f.Confirm(package: package)); var intent = f.Intent(); f.Clear();
        Require(intent.PreviousSnapshotId == intent.Package.Envelope.Identity.SnapshotId
            && intent.Upload!.Previous!.PayloadSha256 != intent.Package.Envelope.PayloadSha256, "fixture did not advance one identity's content");
        if (reopen) f.Open();
        var response = f.Confirm(package: package);
        Require(response.Result.Accepted && response.Applications[0].AppliedSnapshotId == "before"
            && response.NextLineageToken == intent.Package.Envelope.NextLineageToken, "first confirmation of existing identity did not recover");
        Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(f.Confirm(package: package)), "existing identity retry changed receipt");
        var doc = XDocument.Load(Path.Combine(f.Root, "child.rws"));
        doc.Descendants("clashOfRimLineageToken").Single().Value = response.NextLineageToken!;
        doc.Descendants("ticksGame").Single().Value = "1001";
        string path = Path.Combine(f.Root, "next.rws"); doc.Save(path);
        var next = SaveSnapshotPackageBuilder.FromFile(path, package.Envelope.Identity, DateTimeOffset.UtcNow);
        var nextResponse = f.Confirm(package: next);
        Require(nextResponse.Result.Accepted && nextResponse.NextLineageToken != response.NextLineageToken
            && Directory.EnumerateFiles(Path.Combine(f.Journal, "confirmation-receipts"), "*.json").Count() == 2,
            "same identity with valid newer lineage was mistaken for an old receipt");
        f.Open();
        Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(f.Confirm(package: package))
            && JsonSerializer.Serialize(nextResponse) == JsonSerializer.Serialize(f.Confirm(package: next))
            && f.Files.GetLatest("userA", "colonyA")!.Envelope.PayloadSha256 == next.Envelope.PayloadSha256,
            "versioned receipts did not preserve both results without rollback");
    }

    private static void Corrupt(Fixture f, string corrupt)
    {
        f.Fail("ledger"); ExpectFailure(() => f.Confirm()); f.Clear();
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var mutation = node["Upload"]!["Confirmation"]!;
        if (corrupt == "version") node["Version"] = 10;
        else if (corrupt == "sources") mutation["Sources"]![0]!["EventId"] = "other";
        else if (corrupt == "results") mutation["Plan"]!["Results"] = new JsonArray();
        else if (corrupt == "changes") mutation["Plan"]!["Changes"]![0]!["Updated"]!["Target"]!["UserId"] = "intruder";
        else if (corrupt is "receipt" or "receipt-name")
        {
            f.State.SnapshotMutations.Recover();
            path = Directory.EnumerateFiles(Path.Combine(f.Journal, "confirmation-receipts"), "*.json").Single();
            node = JsonNode.Parse(File.ReadAllText(path))!;
            if (corrupt == "receipt") node["Version"] = 99;
        }
        if (corrupt is "intent-name" or "receipt-name")
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "wrong-identity.json"));
        else File.WriteAllText(path, node.ToJsonString());
        ExpectFailure(() => SnapshotCommitService.ValidateDirectory(f.Journal));
        ExpectFailure(() => new SnapshotCommitService(f.Journal));
    }

    private static void InterruptedTemporaryFiles(Fixture f)
    {
        f.Fail("before"); ExpectFailure(() => f.Confirm()); f.Clear();
        var intent = f.Intent();
        File.WriteAllText(Path.Combine(f.Journal, intent.Id + ".json.tmp"), "incomplete intent");
        string receipts = Path.Combine(f.Journal, "confirmation-receipts");
        Directory.CreateDirectory(receipts);
        File.WriteAllText(Path.Combine(receipts, "incomplete.json.tmp"), "incomplete receipt");
        SnapshotCommitService.ValidateDirectory(f.Journal);
        f.Open();
        var response = f.Confirm();
        Require(response.Result.Accepted, "temporary files blocked durable intent recovery");
        VerifyCompleted(f, intent, response);
        SnapshotCommitService.ValidateDirectory(f.Journal);
        f.Open();
        Require(JsonSerializer.Serialize(f.Confirm()) == JsonSerializer.Serialize(response),
            "temporary receipt replaced the original completed results");
    }

    private static void BatchSemantics(string storage) => WithDirectory(root =>
    {
        IAuthoritativeEventLedger Open() => storage == "memory" ? new InMemoryAuthoritativeEventLedger()
            : storage == "file" ? new FileAuthoritativeEventLedger(Path.Combine(root, "events.json"))
            : new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite"));
        var ledger = Open(); var source = Fixture.Gift("gift"); ledger.Append(source);
        var updated = source with { LastApplicationResult = EventApplicationResultKind.NeedsManualReview, LastFailureReason = "rejected" };
        var batch = new LedgerBatch([], [], ConfirmationChanges: [new(source, updated)]);
        ledger.ApplyBatch(batch); if (storage != "memory") ledger = Open(); ledger.ApplyBatch(batch);
        Require(SnapshotConfirmationMutation.Same(updated, ledger.Find("gift")!), "confirmation batch replay changed decision");
        ExpectFailure(() => ledger.ApplyBatch(new LedgerBatch([], [], ConfirmationChanges: [new(updated, updated with { Target = new("intruder") })])));
        ledger.ReportApplicationResult("gift", EventApplicationResultKind.SnapshotBaseMismatch, "later", null);
        ExpectFailure(() => ledger.ApplyBatch(batch));
        Require(ledger.Find("gift")!.LastFailureReason == "later", "confirmation replay overwrote later report");
    });

    private sealed class Fixture : IDisposable
    {
        public readonly string Root, Database, Journal;
        public readonly bool Single;
        public readonly SaveSnapshotPackage Before, Package;
        public ClashOfRimNetworkState State = null!;
        public FileColonySnapshotIndexStore Files = null!;
        public int PostCalls;
        public Action? OnPrepare, OnPost;
        private string? fault;
        private CrashStore store = null!;
        private FileStream? held;
        public IReadOnlyList<ConfirmEventApplicationEntry> Entries => Single ? [new("trade", "source", "exchanged")]
            : [new("gift", null, "ItemDeliveryAnchored"), new("trade", "source", "exchanged"), new("support", null, "SupportPawnArrived"),
                new("reject", null, "not-anchored"), new("missing", "source", ""), new("foreign", null, "ItemDeliveryAnchored"),
                new("cancelled", null, "ItemDeliveryAnchored"), new("trade", "source", "exchanged")];
        public Fixture(string root, bool single, SaveSnapshotPackage package)
        {
            Root = root; Single = single; Database = Path.Combine(root, "server.sqlite"); Journal = Path.Combine(root, "intents");
            ServerDatabaseMigrator.Migrate(Database); Open();
            var accepted = new SnapshotUploadReceiver(Files).Receive(new("userA", "colonyA", "before"), package, DateTimeOffset.UtcNow).AcceptedSnapshot!;
            Before = Files.GetLatestPackage("userA", "colonyA")!;
            using var input = new MemoryStream(package.Payload); using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var document = XDocument.Load(gzip);
            document.Root!.Element("game")!.Add(new XElement("components", new XElement("li", new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
                new XElement("clashOfRimLineageSnapshotId", "before"), new XElement("clashOfRimLineageToken", accepted.Envelope.NextLineageToken))));
            string child = Path.Combine(root, "child.rws"); document.Save(child);
            Package = SaveSnapshotPackageBuilder.FromFile(child, new("userA", "colonyA", "confirmed"), DateTimeOffset.UtcNow);
            State.Ledger.Append(Gift("gift")); State.Ledger.Append(Gift("reject"));
            State.Ledger.Append(Gift("foreign") with { Target = new("intruder", "other") });
            State.Ledger.Append(Gift("cancelled") with { Status = ServerEventStatus.Cancelled });
            State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, new("userA", "colonyA"), new("other", "other-colony"), "trade", false,
                new TradeEventPayload("source", TradeStage.SelfDeliveryExchange, [], [new("payment", "Silver", 1, "before")], 0), DateTimeOffset.UtcNow) with { EventId = "trade" });
            State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, new("other", "other-colony"), new("userA", "colonyA"), "support", false,
                new SupportPawnEventPayload("owner:other/colony:other-colony/pawn:absent", "support-source", "Ally", true, null), DateTimeOffset.UtcNow) with { EventId = "support" });
            foreach (var party in new[] { new EventParty("userA", "colonyA"), new EventParty("other", "other-colony") })
                State.Achievements.RecordCumulativeMetricAchievements(party.UserId, party.ColonyId!, "seed", "prior",
                    new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 9 }, [], DateTimeOffset.UtcNow);
            State.BankLoans.CreateDebt("pending", "userA", "colonyA", "before", 50, BankLoanRegistry.DebtSourceFine, "fine", "source", 1000, DateTimeOffset.UtcNow, true);
        }
        public static AuthoritativeEvent Gift(string id) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("other", "other-colony"), new("userA", "colonyA"),
            id, false, new ItemDeliveryEventPayload([], null), DateTimeOffset.UnixEpoch) with
            { EventId = id, DeliveredToSnapshotId = "before", DeliveredAtUtc = DateTimeOffset.UnixEpoch };
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(Root, "snapshots")); store = new(Files);
            var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, LedgerProxy>();
            var interceptor = (LedgerProxy)proxy; interceptor.Inner = new SqliteAuthoritativeEventLedger(Database);
            interceptor.After = () => { if (fault == "after-ledger") throw new IOException("after ledger"); if (fault == "ack") Hold(); };
            State = new(snapshotStore: store, ledger: proxy, snapshotCommits: new SnapshotCommitService(Journal),
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.BankLoans), null),
                achievements: new AchievementRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.Achievements), null),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.confirm", "confirm", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new Processor(() => { PostCalls++; OnPost?.Invoke(); if (fault == "processor") throw new IOException("processor"); if (fault == "plan") Hold(); })],
                    SaveIndexExtensions = [new PrepareHook(() => OnPrepare?.Invoke())] }]));
        }
        private void Hold() => held ??= new FileStream(Directory.EnumerateFiles(Journal, "*.json").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
        public ConfirmEventApplicationsResponse Confirm(IReadOnlyList<ConfirmEventApplicationEntry>? entries = null, string baseId = "before", SaveSnapshotPackage? package = null)
        {
            package ??= Package; entries ??= Entries;
            string token = TradeCommandAuthTests.Token(State, "userA", "colonyA");
            if (!Single) return State.EventConfirmations.Confirm(new ConfirmEventApplicationsMetadataRequest(
                "request", "userA", "colonyA", baseId, ProtocolDtoMapper.ToMetadataDto(package), entries, token), package.Payload);
            var entry = entries[0];
            var result = State.EventConfirmations.Confirm(new ConfirmEventApplicationMetadataRequest(
                "request", entry.EventId, entry.SourceEventId, "userA", "colonyA", baseId,
                ProtocolDtoMapper.ToMetadataDto(package), entry.ClientApplicationResult, token), package.Payload, token);
            return new(result.Result, result.AppliedSnapshotId, [new(result.EventId, result.Result, result.AppliedSnapshotId, result.ServerValidationResult)], result.NextLineageToken);
        }
        public SnapshotCommitIntent Intent() => JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(Directory.EnumerateFiles(Journal, "*.json").Single()))!;
        public void Fail(string failure)
        {
            fault = failure; store.Fault = failure;
            if (fault == "receipt") File.WriteAllText(Path.Combine(Journal, "confirmation-receipts"), "blocked");
            string? table = failure switch { "bank" => "server_bank_debts", "players" => "server_players", "ledger" => "events",
                "achievement" => "server_achievement_metric_events", "notification" => "events", _ => null };
            if (table is not null) Sql(Database, $"create trigger fail_confirmation before {(fault == "ledger" ? "update" : "insert")} on {table} "
                + (fault == "ledger" ? $"when new.event_id = '{(Single ? "trade" : "reject")}' " : "")
                + "begin select raise(abort, 'injected confirmation failure'); end;");
        }
        public void Clear()
        {
            if (fault == "receipt") File.Delete(Path.Combine(Journal, "confirmation-receipts"));
            held?.Dispose(); held = null; fault = null; store.Fault = null; Sql(Database, "drop trigger if exists fail_confirmation;");
        }
        public void Dispose() => held?.Dispose();
    }
    public class LedgerProxy : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!;
        public Action? After;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (method!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch) && args![0] is LedgerBatch { ConfirmationChanges: not null }) After?.Invoke();
            return result;
        }
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
    private sealed class Processor(Action action) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.confirmation";
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.Notification;
        public int Order => 100;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) => action();
    }
    private sealed class PrepareHook(Action action) : ISaveIndexExtension
    { public IEnumerable<ThingSummary> ReadContainedThings(XElement element, ThingSummary thing, SaveIndexReadContext context) { action(); return []; } }
    private static void ExpectFailure(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException) { return; } throw new Exception("Expected confirmation fault was not reached."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Confirmation journal: " + message); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithFixture(Func<string, SaveSnapshotPackage> fixture, bool single, Action<Fixture> action) => WithDirectory(root =>
    { using var f = new Fixture(root, single, fixture("before")); action(f); });
    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-confirmation-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
