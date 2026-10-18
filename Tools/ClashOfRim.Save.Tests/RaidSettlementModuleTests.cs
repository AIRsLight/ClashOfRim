using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RaidSettlementModuleTests
{
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var origin in Enum.GetValues<RaidSettlementOrigin>())
                foreach (double ratio in new[] { 0d, 1d }) Settlement(origin, ratio);
                DeferredReplay();
            }
            foreach (string change in new[] { "none", "newer", "completed" }) RecoveryPreparation(change);
            foreach (string change in new[] { "source", "attacker", "defender", "evidence", "baseline" }) RejectMismatches(change);
            ReconcileArtifacts();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Settlement(RaidSettlementOrigin origin, double ratio)
    {
        using var f = new Fixture();
        f.Configuration = new(raidSettlementLossRatio: ratio);
        var payload = f.Data.Payload with { Origin = origin, RequestHash = "bound-evidence" };
        int reads = 0;
        var result = f.Settlement.Execute(payload, () =>
        {
            Check(!Monitor.IsEntered(f.Gate), "Evidence reading held the mutation gate.");
            reads++; return f.Data.Evidence;
        }, f.Data.Now);
        Check(result.Kind == RaidSettlementOperationResultKind.Completed && f.Recovery.Replays == 2 && reads == 1,
            "Independent settlement failed or repeated recovery: " + result.Message);
        var snapshot = f.Snapshots.GetLatestPackage("defender", "defender-colony")!;
        Check(snapshot.Index.Things.Sum(thing => int.TryParse(thing.StackCount, out int count) ? count : 1) == (ratio == 0 ? 10 : 0),
            "Settlement ignored current loss policy or remote evidence map.");
        Check(snapshot.Envelope.PreviousSnapshotId == "before" && snapshot.Envelope.Identity.SnapshotId == result.DefenderSnapshotId
            && f.Ledger.Find(f.Data.Raid.EventId)!.AppliedSnapshotId == result.DefenderSnapshotId
            && f.Notifications.GetVersion("defender") == 1, "Settlement lost lineage, ledger batch or notification.");
        var settled = (RaidEventPayload)f.Ledger.Find(result.SettlementEventId!)!.Payload;
        Check(settled.EvidenceRequestHash == (origin == RaidSettlementOrigin.OnlineEvidence ? "bound-evidence" : null),
            "Offline timeout inherited an online evidence receipt.");
        string before = JsonSerializer.Serialize(f.Ledger.ListAll());
        f.Configuration = new(raidSettlementLossRatio: ratio == 0 ? 1 : 0);
        var replay = f.Settlement.Execute(payload, () => throw new Exception("Completed settlement read deleted evidence."), f.Data.Now.AddDays(1));
        Check(replay.Kind == RaidSettlementOperationResultKind.AlreadyCompleted && replay.DefenderSnapshotId == result.DefenderSnapshotId
            && replay.SettlementEventId == result.SettlementEventId && f.Recovery.Replays == 3
            && before == JsonSerializer.Serialize(f.Ledger.ListAll()) && f.Notifications.GetVersion("defender") == 1,
            "Completed settlement was recalculated after configuration changed.");
    }

    private static void DeferredReplay()
    {
        using var f = new Fixture();
        f.Artifacts.Store(f.Data.Payload.EvidenceArtifactId, f.Data.Evidence);
        f.Work.Process(f.Data.Payload, f.Data.Now);
        Check(f.Recovery.Replays == 2 && f.Artifacts.Exists(f.Data.Payload.EvidenceArtifactId),
            "Deferred worker duplicated recovery or removed evidence before completion.");
        f.Work.Complete(f.Data.Payload);
        Check(!f.Artifacts.Exists(f.Data.Payload.EvidenceArtifactId), "Completion left the evidence artifact behind.");
        f.Work.Process(f.Data.Payload, f.Data.Now.AddDays(1));
        Check(f.Recovery.Replays == 3 && f.Ledger.ListAll().Count == 2, "Completed job required its deleted artifact.");
    }

    private static void RecoveryPreparation(string change)
    {
        using var f = new Fixture();
        int captures = 0;
        f.Recovery.OnCapture = () => { if (++captures == 2) f.Recovery.NeedsPreparation = true; };
        RaidSettlementOperationResult? result = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Recovery.OnPrepare = hook,
            () => result = f.Settlement.Execute(f.Data.Payload, () => f.Data.Evidence, f.Data.Now), () =>
            {
                Check(f.Ledger.ListAll().Count == 1 && f.Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "before",
                    "Settlement published before recovery preparation completed.");
                if (change == "newer")
                {
                    var newer = f.Data.Original with { Envelope = f.Data.Original.Envelope with { Identity = new("defender", "defender-colony", "newer") } };
                    f.Snapshots.StoreLatest(newer, newer.Index, f.Data.Now);
                }
                if (change == "completed")
                {
                    f.Ledger.MarkDelivered(f.Data.Raid.EventId, "elsewhere", f.Data.Now);
                    f.Ledger.MarkApplied(f.Data.Raid.EventId, "elsewhere", f.Data.Now);
                }
            });
        var expected = change switch { "none" => RaidSettlementOperationResultKind.Completed,
            "newer" => RaidSettlementOperationResultKind.ManualReview, _ => RaidSettlementOperationResultKind.AlreadyCompleted };
        Check(result?.Kind == expected && f.Recovery.Replays == 2 && captures == 2,
            "Final commit kept an outer lock, repeated recovery or skipped revalidation: " + change);
        if (change == "newer") Check(f.Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "newer"
            && f.Ledger.ListAll().Count == 1, "Recovery overwrote a newer defender snapshot.");
    }

    private static void RejectMismatches(string change)
    {
        using var f = new Fixture();
        var payload = change switch {
            "source" => f.Data.Payload with { RaidEventId = "missing" },
            "attacker" => f.Data.Payload with { AttackerColonyId = "rebuilt" },
            "defender" => f.Data.Payload with { DefenderColonyId = "rebuilt" },
            "baseline" => f.Data.Payload with { DefenderSnapshotId = "other-base" }, _ => f.Data.Payload };
        var evidence = change == "evidence" ? f.Data.Evidence with { Envelope = f.Data.Evidence.Envelope with { Identity = new("intruder", "colony", "evidence") } } : f.Data.Evidence;
        f.Artifacts.Store(payload.EvidenceArtifactId, evidence);
        bool review = false;
        try { f.Work.Process(payload, f.Data.Now); } catch (SnapshotPostUploadManualReviewException) { review = true; }
        Check(review && f.Ledger.ListAll().Count == 1 && f.Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "before"
            && f.Artifacts.Exists(payload.EvidenceArtifactId) && f.Notifications.GetVersion("defender") == 0,
            "Mismatched settlement mutated the defender or discarded review evidence: " + change);
    }

    private static void ReconcileArtifacts()
    {
        using var f = new Fixture();
        var store = new SqliteSnapshotPostUploadJobStore(Path.Combine(f.Root, "jobs.sqlite"));
        foreach (var state in Enum.GetValues<SnapshotPostUploadJobState>())
        {
            string id = state.ToString();
            var payload = f.Data.Payload with { EvidenceArtifactId = id };
            f.Artifacts.Store(id, f.Data.Evidence);
            store.Upsert(new(id, RaidSettlementWork.ProcessorId, SnapshotPostUploadKind.RaidSettlementEvidence, "attacker", "attacker-colony",
                null, "evidence", f.Data.Now, payload.Serialize(), state, 0, f.Data.Now, null));
        }
        f.Artifacts.Store("orphan", f.Data.Evidence);
        var jobs = new SnapshotPostUploadJobRegistry(store);
        var work = new RaidSettlementWork(f.Settlement, f.Artifacts, jobs, f.Mutations);
        work.ReconcileArtifacts();
        Check(!f.Artifacts.Exists("orphan") && jobs.ListAll().All(job => f.Artifacts.Exists(job.JobId)) && f.Recovery.Replays == 0,
            "Artifact reconciliation deleted referenced work or started unrelated snapshot recovery.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "cor-settlement-module-" + Guid.NewGuid().ToString("N"));
        internal readonly object Gate = new();
        internal readonly EconomicModuleTestContext.RecoveryProbe Recovery = new();
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly InMemorySnapshotPostUploadArtifactStore Artifacts = new();
        internal readonly EventNotificationHub Notifications = new();
        internal readonly FileColonySnapshotIndexStore Snapshots;
        internal readonly SnapshotMutationCoordinator Mutations;
        internal readonly RaidSettlementTestData Data;
        internal readonly RaidSettlement Settlement;
        internal readonly RaidSettlementWork Work;
        internal ClashOfRimServerConfiguration Configuration = new();
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Data = new(Root);
            Snapshots = new(Path.Combine(Root, "snapshots"));
            Snapshots.StoreLatest(Data.Original, Data.Original.Index, Data.Now);
            Ledger.Append(Data.Raid);
            Mutations = new(Gate, Recovery);
            Settlement = new(Ledger, Snapshots, Mutations, new SnapshotCommitService(), new BankLoanRegistry(), new PlayerRegistry(),
                Notifications, new OnlinePresenceRegistry(), new AdminBaselineRegistry(), new ServerPluginRegistry(),
                new CompatibilityBaselineRegistry(), () => Configuration, () => NullLogger.Instance);
            Work = new(Settlement, Artifacts, new SnapshotPostUploadJobRegistry(), Mutations);
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
