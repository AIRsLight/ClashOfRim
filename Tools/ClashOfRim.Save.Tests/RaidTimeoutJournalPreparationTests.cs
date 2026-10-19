using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class RaidTimeoutJournalPreparationTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string phase in new[] { "classify", "pending", "schedule-capture", "schedule-commit", "cleanup-capture", "cleanup-commit", "complete" })
        {
            foreach (string change in new[] { "none", "compete", "newer", "failure" })
                RaidTimeoutFixture.With(original, f => VerifyJournalPreparation(f, phase, change));
            if (phase is "schedule-commit" or "cleanup-capture" or "cleanup-commit" or "complete")
                foreach (string change in new[] { "source", "snapshot" })
                    RaidTimeoutFixture.With(original, f => VerifyJournalPreparation(f, phase, change));
            if (phase is "cleanup-capture" or "cleanup-commit")
                RaidTimeoutFixture.With(original, f => VerifyJournalPreparation(f, phase, "job"));
        }
    }

    private static void VerifyJournalPreparation(RaidTimeoutFixture f, string phase, string change)
    {
        bool full = phase is "classify" or "pending" or "complete";
        if (phase == "complete")
        {
            f.State.Ledger.Append(f.Raid with { EventId = "later", IdempotencyKey = "later", Actor = new("later", "later-colony"),
                CreatedAtUtc = f.Raid.CreatedAtUtc.AddSeconds(1) });
            f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        }
        if (phase != "classify") RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now,
            isAttackerOnline: _ => phase == "complete");
        var source = f.State.Ledger.Find(f.Raid.EventId)!;
        if (phase.StartsWith("cleanup")) f.State.RaidOfflineEvidence.Schedule(source, f.Now);
        var foreign = f.CreateForeignIntent();
        Action? recoveryHook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation()) f.SeedForeign(foreign);
            f.Store.BeforePrepare = () => recoveryHook?.Invoke();
        }
        if (phase is "schedule-commit") f.Artifacts.AfterPrepare = () => { f.Artifacts.AfterPrepare = null; Inject(); };
        else if (phase is "cleanup-commit") f.Store.BeforePrepare = () => { f.Store.BeforePrepare = null; Inject(); };
        else if (phase == "complete") f.Store.BeforeRead = owner => { if (owner == "later") { f.Store.BeforeRead = null; Inject(); } };
        else Inject();

        Exception? failure = null; string? before = null; int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => recoveryHook = hook, () =>
        {
            try
            {
                if (phase == "classify") f.Reconcile();
                else if (phase is "pending" or "complete") f.State.RaidTimeouts.RecoverPending(f.Now);
                else if (phase.StartsWith("schedule")) f.State.RaidOfflineEvidence.Schedule(source, f.Now);
                else f.State.RaidBattleCleanup.CleanupTimeouts(new[] { source }, f.Now);
            }
            catch (Exception ex) { failure = ex; }
        }, () =>
        {
            captures++;
            Check(f.Store.WritesByOwner.GetValueOrDefault("foreign") == 0 && f.State.Ledger.FindByIdempotencyKey("foreign") is null,
                "Maintenance recovery published before preparation completed.");
            if (change == "compete") f.State.SnapshotMutations.Recover();
            if (change == "failure") f.Store.FailPreparation = true;
            if (change == "newer") f.Files.StoreLatest(foreign.Package with { Envelope = foreign.Package.Envelope with
                { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, foreign.Package.Index, f.Now);
            if (change == "source")
            {
                var current = f.State.Ledger.Find(f.Raid.EventId)!;
                var payload = (RaidEventPayload)current.Payload;
                f.State.Ledger.ApplyBatch(new LedgerBatch([], [new(current.EventId, current.Status, current.Status, payload,
                    payload with { TimeoutWork = payload.TimeoutWork! with { TimedOutAtUtc = f.Now.AddMinutes(1) } }, current)]));
            }
            if (change == "snapshot")
            {
                var package = f.Files.GetLatestPackage("attacker", "colony-a")!;
                f.Files.StoreLatest(package with { Envelope = package.Envelope with { Identity = package.Envelope.Identity with { SnapshotId = "later-upload" } } }, package.Index, f.Now);
            }
            if (change == "job") f.State.SnapshotPostUploadJobs.MarkManualReview("raid-settlement:" + f.Raid.EventId, "changed while recovering");
            before = f.JournalEvidence();
        });
        Check(captures == 1, "Maintenance did not reach the expected recovery boundary: " + phase);
        bool failed = change is "newer" or "failure";
        Check((failure is not null) == failed, "Unexpected maintenance recovery result: " + phase + "/" + change + " " + failure);
        if (failed)
        {
            Check(before == f.JournalEvidence() && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 0 && f.State.SnapshotCommits.HasPending,
                "Failed maintenance recovery changed business or discarded its intent.");
            if (change == "failure")
            {
                f.Open(); f.State.SnapshotMutations.Recover();
                Check(!f.State.SnapshotCommits.HasPending && f.State.Ledger.FindByIdempotencyKey("foreign") is not null,
                    "Preparation failure could not resume after reopening.");
            }
        }
        else
        {
            Check(!f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
                && f.Files.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && JsonSerializer.Serialize(f.State.Ledger.FindByIdempotencyKey("foreign")) == JsonSerializer.Serialize(foreign.Event),
                "Maintenance did not publish the original foreign intent exactly once.");
            bool changed = change is "source" or "snapshot" or "job";
            var current = (RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload;
            Check(current.TimeoutWork!.Completed == (full && !changed), "Maintenance acknowledged a stale timeout decision: " + phase + "/" + change);
            if (changed && phase.StartsWith("cleanup")) Check(f.Store.WritesByOwner.GetValueOrDefault("attacker") == 0,
                "Cleanup published after source, snapshot or task changed during recovery.");
            if (changed && phase == "schedule-commit") Check(f.State.SnapshotPostUploadJobs.ListAll().Count == 0,
                "Scheduling published stale evidence after recovery.");
            if (!changed && phase.StartsWith("schedule")) Check(f.State.SnapshotPostUploadJobs.ListReady(f.Now).Count == 1
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == 0, "Evidence scheduling did not preserve its order before cleanup.");
        }
        Check(!Directory.EnumerateFiles(Path.Combine(f.Root, "artifacts"), "*.tmp").Any(), "Recovery leaked prepared evidence files.");
        GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Maintenance leaked its mutation gate.");
    }
}
