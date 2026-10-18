using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class RaidEvidenceSchedulingModuleTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool prepared in new[] { false, true }) PublishAndReplay(prepared);
            }
            foreach (string mismatch in new[] { "kind", "source", "user", "colony", "snapshot", "identity", "envelope", "bytes" })
                foreach (bool prepared in new[] { false, true }) RejectMismatch(mismatch, prepared);
            foreach (int boundary in new[] { 1, 2 })
                foreach (string change in new[] { "none", "newer", "manual", "completed", "artifact" }) RecoverPrepared(boundary, change);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void PublishAndReplay(bool prepared)
    {
        using var f = new Fixture();
        SnapshotPostUploadJobRecord job;
        if (prepared)
        {
            using var preparation = f.WithMutation(() => f.Scheduling.Capture(f.Request));
            f.WithMutation(() => Reject(() => preparation.Prepare(null)));
            preparation.Prepare(null);
            f.CheckUnpublished();
            var otherOwner = f.NewScheduling();
            f.WithMutation(() => Reject(() => preparation.Commit(otherOwner, preparation.Envelope, f.Data.Now)));
            f.CheckUnpublished();
            job = f.WithMutation(() => preparation.Commit(f.Scheduling, preparation.Envelope, preparation.OccurredAtUtc));
            f.WithMutation(() => Reject(() => preparation.Commit(f.Scheduling, preparation.Envelope, f.Data.Now)));
        }
        else
        {
            job = f.Scheduling.Schedule(f.Request);
            Check(f.Recovery.Replays == 1, "Synchronous scheduling repeated snapshot recovery.");
        }
        f.CheckPublished(job);
        f.Open();
        Check(f.Jobs.Find(job.JobId) == job, "Ready job changed after reopening SQLite storage.");
        f.CheckPublished(job);
        var retry = f.Request with { Job = f.Request.Job with { SessionId = "new-session", OccurredAtUtc = f.Data.Now.AddDays(1) } };
        SnapshotPostUploadJobRecord replay;
        if (prepared)
        {
            // The prepared HTTP path tolerates a reconstructed timestamp/token, but returns the first receipt.
            retry = retry with { Snapshot = retry.Snapshot with { Envelope = retry.Snapshot.Envelope with
                { CreatedAtUtc = f.Data.Now.AddDays(1), NextLineageToken = "retry-token" } } };
            using var preparation = f.WithMutation(() => f.Scheduling.Capture(retry));
            preparation.Prepare(null);
            Check(preparation.IsReplay && preparation.OccurredAtUtc == job.OccurredAtUtc
                && preparation.Envelope == f.Request.Snapshot.Envelope, "Prepared replay lost the original evidence receipt.");
            replay = f.WithMutation(() => preparation.Commit(f.Scheduling, preparation.Envelope, preparation.OccurredAtUtc));
        }
        else replay = f.Scheduling.Schedule(retry);
        Check(replay == job && f.Jobs.ListAll().Count == 1, "Retry replaced original job metadata or added a duplicate.");
        var conflict = f.Request with { RequestHash = "different-request" };
        if (prepared)
        {
            using var preparation = f.WithMutation(() => f.Scheduling.Capture(conflict));
            Reject(() => preparation.Prepare(null));
        }
        else Reject(() => f.Scheduling.Schedule(conflict));
        f.CheckPublished(job);
    }

    private static void RejectMismatch(string mismatch, bool prepared)
    {
        using var f = new Fixture();
        var request = mismatch switch
        {
            "kind" => f.Request with { Job = f.Request.Job with { Kind = SnapshotPostUploadKind.AuthoritativeColonySnapshot } },
            "source" => f.Request with { RaidEventId = "missing" },
            "user" => f.Request with { Job = f.Request.Job with { UserId = "intruder" } },
            "colony" => f.Request with { Job = f.Request.Job with { ColonyId = "rebuilt" } },
            "snapshot" => f.Request with { Job = f.Request.Job with { SnapshotId = "other" } },
            "identity" => f.Request with { Snapshot = f.Request.Snapshot with { Identity = new("intruder", "colony", "evidence") } },
            "envelope" => f.Request with { Snapshot = f.Request.Snapshot with { Envelope = f.Request.Snapshot.Envelope with
                { Identity = new("intruder", "colony", "evidence") } } },
            _ => f.Request with { EvidencePayload = [0] }
        };
        Reject(() =>
        {
            if (!prepared) { f.Scheduling.Schedule(request); return; }
            using var preparation = f.WithMutation(() => f.Scheduling.Capture(request));
            preparation.Prepare(null);
        });
        f.CheckUnpublished();
    }

    private static void RecoverPrepared(int boundary, string change)
    {
        using var f = new Fixture();
        var job = f.SeedPrepared();
        f.Open();
        Check(f.Jobs.ListReady(DateTimeOffset.MaxValue).Count == 0, "Prepared evidence became executable on reopen.");
        int captures = 0;
        f.Recovery.OnCapture = () => { if (++captures == boundary) f.Recovery.NeedsPreparation = true; };
        SnapshotPostUploadJobRecord? reply = null;
        Exception? error = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Recovery.OnPrepare = hook, () =>
            {
                try { reply = f.Scheduling.RecoverPrepared(job, f.Data.Now.AddMinutes(1)); }
                catch (Exception ex) { error = ex; }
            }, () =>
            {
                Check(f.Jobs.Find(job.JobId)!.State == SnapshotPostUploadJobState.Prepared
                    && f.Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == "attack"
                    && f.Players.FindByUserId("attacker")!.CurrentSnapshotId == "attack", "Recovery published before preparation completed.");
                if (change == "newer")
                {
                    var newer = f.Before with { Envelope = f.Before.Envelope with { Identity = new("attacker", "attacker-colony", "newer") } };
                    f.Snapshots.StoreLatest(newer, newer.Index, f.Data.Now);
                    f.Players.RecordLatestSnapshotReference("attacker", "attacker-colony", "newer", f.Data.Now);
                }
                if (change == "manual") f.Jobs.MarkManualReview(job.JobId, "review");
                if (change == "completed") f.Jobs.MarkCompleted(job.JobId);
                if (change == "artifact") f.Artifacts.Delete(f.ArtifactId);
            });
        Check((error is not null) == (change is "newer" or "artifact"), "Recovery returned the wrong conflict result: " + change + ": " + error);
        if (change == "none")
        {
            f.CheckPublished(reply!);
            Check(captures == 2 && f.Recovery.Replays == 2, "Standalone recovery repeated a boundary or skipped its preparation.");
        }
        else
        {
            var current = f.Jobs.Find(job.JobId);
            Check(current?.State == (change == "completed" ? null : change == "manual" ? SnapshotPostUploadJobState.ManualReview : SnapshotPostUploadJobState.Prepared),
                "Recovery revived or advanced an obsolete job.");
            string snapshot = change == "newer" ? "newer" : "attack";
            Check(f.Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == snapshot
                && f.Players.FindByUserId("attacker")!.CurrentSnapshotId == snapshot, "Recovery overwrote a newer snapshot or player reference.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "cor-evidence-module-" + Guid.NewGuid().ToString("N"));
        internal readonly object Gate = new();
        internal readonly EconomicModuleTestContext.RecoveryProbe Recovery = new();
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly SnapshotMutationCoordinator Mutations;
        internal readonly RaidSettlementTestData Data;
        internal readonly SaveSnapshotPackage Before;
        internal readonly RaidEvidenceSchedule Request;
        internal FileColonySnapshotIndexStore Snapshots = null!;
        internal FileSnapshotPostUploadArtifactStore Artifacts = null!;
        internal SnapshotPostUploadJobRegistry Jobs = null!;
        internal PlayerRegistry Players = null!;
        internal RaidEvidenceScheduling Scheduling = null!;
        internal string ArtifactId => RaidEvidenceRules.BuildArtifactId(Data.Raid.EventId, "evidence");

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Data = new(Root);
            Mutations = new(Gate, Recovery);
            Before = Data.Evidence with { Envelope = Data.Evidence.Envelope with
                { Identity = new("attacker", "attacker-colony", "attack"), NextLineageToken = "attack-token" } };
            var evidence = Data.Evidence with { Envelope = Data.Evidence.Envelope with { PreviousSnapshotId = "attack", LineageToken = "attack-token" } };
            Request = new(Data.Raid.EventId, new(evidence.Envelope.Identity, evidence.Envelope, evidence.Index, Data.Now),
                new(SnapshotPostUploadKind.RaidSettlementEvidence, "attacker", "attacker-colony", "session", "evidence", Data.Now),
                evidence.Payload, RaidSettlementOrigin.OnlineEvidence, "Applied", "request-binding");
            Ledger.Append(Data.Raid);
            Open();
            Snapshots.StoreLatest(Data.Original, Data.Original.Index, Data.Now);
            Snapshots.StoreLatest(Before, Before.Index, Data.Now);
            Players.RecordLatestSnapshotReference("attacker", "attacker-colony", "attack", Data.Now);
        }

        internal void Open()
        {
            Snapshots = new(Path.Combine(Root, "snapshots"));
            Artifacts = new(Path.Combine(Root, "artifacts"));
            Jobs = new(new SqliteSnapshotPostUploadJobStore(Path.Combine(Root, "jobs.sqlite")));
            Players = new(Path.Combine(Root, "players.json"));
            Scheduling = NewScheduling();
        }

        internal RaidEvidenceScheduling NewScheduling() => new(Ledger, Snapshots, Artifacts, Jobs, Players, Mutations);
        internal T WithMutation<T>(Func<T> action)
        {
            using var mutation = Mutations.EnterUnrecovered();
            lock (Jobs.SnapshotCommitGate) return action();
        }
        internal void WithMutation(Action action) => WithMutation(() => { action(); return true; });

        internal SnapshotPostUploadJobRecord SeedPrepared()
        {
            var payload = Data.Payload with { EvidenceArtifactId = ArtifactId, RequestHash = Request.RequestHash,
                Publication = new(Before.Envelope, Request.Snapshot.Envelope) };
            Artifacts.Store(ArtifactId, new(Request.Snapshot.Envelope, Request.EvidencePayload, Request.Snapshot.Index));
            return Jobs.EnqueuePrepared("raid-settlement:" + Data.Raid.EventId, RaidSettlementWork.ProcessorId, Request.Job, payload.Serialize());
        }

        internal void CheckUnpublished() => Check(Jobs.ListAll().Count == 0 && !Artifacts.Exists(ArtifactId)
            && Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == "attack"
            && Players.FindByUserId("attacker")!.CurrentSnapshotId == "attack", "Preparation or rejection published evidence prematurely.");

        internal void CheckPublished(SnapshotPostUploadJobRecord job) => Check(job.State == SnapshotPostUploadJobState.Ready
            && job.Kind == Request.Job.Kind && job.SessionId == Request.Job.SessionId && job.OccurredAtUtc == Data.Now
            && Artifacts.Read(ArtifactId)!.Envelope == Request.Snapshot.Envelope
            && Snapshots.GetLatest("attacker", "attacker-colony")!.Envelope == Request.Snapshot.Envelope
            && Players.FindByUserId("attacker")!.CurrentSnapshotId == "evidence"
            && Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "before" && Ledger.ListAll().Count == 1,
            "Evidence publication lost metadata or performed settlement early.");

        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return; }
        throw new Exception("Expected evidence scheduling to reject the request.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
