using static SnapshotTestAssertions;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class DownloadCleanupJournalRecoveryTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool final in new[] { false, true })
        {
            foreach (string change in new[] { "none", "compete", "newer", "failure", "snapshot", "removed", "owner", "new-raid" })
                RaidCleanupFixture.With(original, false, f => VerifyDownloadRecovery(f, final, change));
            if (final)
                foreach (string change in new[] { "source", "support-closed", "delivery" })
                    RaidCleanupFixture.With(original, false, f => VerifyDownloadRecovery(f, final, change));
            RaidCleanupFixture.With(original, false, f => VerifyDownloadRecovery(f, final, "own-cleanup"));
        }
    }

    private static void VerifyDownloadRecovery(RaidCleanupFixture f, bool final, string change)
    {
        bool own = change == "own-cleanup";
        var foreign = f.DownloadForeignIntent();
        Action? recoveryHook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation()) f.SeedDownloadRecovery(foreign, own);
            f.Store.BeforePrepare = () => recoveryHook?.Invoke();
        }
        if (final) f.Store.BeforePrepare = Inject;
        else Inject();
        Exception? failure = null;
        string? before = null;
        int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => recoveryHook = hook,
            () => { try { f.InvokeCleanup(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                captures++;
                Check(f.Store.WritesByOwner.Count == 0 && f.State.Ledger.ListAll().Count == 2,
                    "Download cleanup published before journal preparation completed.");
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Files.StoreLatest(foreign.Package with { Envelope = foreign.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, foreign.Package.Index, f.Now);
                if (change == "snapshot") f.Files.StoreLatest(f.Original with { Envelope = f.Original.Envelope with
                    { Identity = new("attacker", "colony-a", "newer-attacker"), NextLineageToken = "newer-attacker" } }, f.Original.Index, f.Now);
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
                if (change == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Failed);
                if (change == "new-raid") f.State.Ledger.Append(f.Raid with { EventId = "new-raid", IdempotencyKey = "new-raid",
                    Status = ServerEventStatus.Recorded, Payload = ((RaidEventPayload)f.Raid.Payload) with
                    { FinishedAtUtc = null, AttackForce = new RaidAttackForceRecord("new-attack", [], []) } });
                if (change == "support-closed") f.State.Ledger.ChangeStatus(f.Support.EventId, ServerEventStatus.Cancelled);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Support.EventId, "delivered-during-recovery", f.Now);
                before = f.DownloadRecoveryEvidence();
            });
        Check(captures == 1, "Download cleanup missed its recovery boundary: " + final);
        bool failed = change is "newer" or "failure";
        Check((failure is not null) == failed, "Unexpected download recovery outcome: " + final + "/" + change + " " + failure);
        if (failed)
        {
            Check(before == f.DownloadRecoveryEvidence() && f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.Count == 0,
                "Failed download recovery changed state or lost its journal.");
            if (change == "failure")
            {
                f.Open(); f.State.SnapshotMutations.Recover();
                Check(!f.State.SnapshotCommits.HasPending && f.State.Ledger.FindByIdempotencyKey("download-foreign") is not null,
                    "Download recovery preparation failure could not resume after reopening.");
            }
        }
        else
        {
            bool cleaned = change is "none" or "compete" or "support-closed" or "delivery" or "own-cleanup";
            Check(!f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("attacker") == (cleaned ? 1 : 0),
                "Download cleanup published a stale decision or duplicated a recovered cleanup.");
            if (!own) Check(f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
                && f.Files.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && JsonSerializer.Serialize(f.State.Ledger.FindByIdempotencyKey("download-foreign")) == JsonSerializer.Serialize(foreign.Event),
                "Download cleanup did not recover the original foreign transaction exactly once.");
            if (cleaned)
            {
                Check(!f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(w => w.Def == "ClashOfRim_RemoteRaidBattleMapParent")
                    && f.State.Ledger.ListAll().Count == (own || change == "support-closed" ? 4 : 5)
                    && f.State.Ledger.Find(f.Support.EventId)!.Status == (change == "support-closed" ? ServerEventStatus.Cancelled : ServerEventStatus.Failed),
                    "Recovered cleanup did not preserve the complete loss batch or current support state.");
                if (own) Check(f.Files.GetLatest("attacker", "colony-a")!.Envelope == f.Cleaned.Envelope,
                    "Download cleanup replaced the already recovered acknowledgement.");
                if (change == "delivery") Check(f.State.Ledger.Find(f.Support.EventId)!.DeliveredToSnapshotId == "delivered-during-recovery",
                    "Download cleanup lost concurrent support delivery metadata.");
            }
            else Check(f.State.Ledger.ListAll().Count == (change == "new-raid" ? 4 : 3)
                && f.State.Ledger.Find(f.Support.EventId)!.Status == f.Support.Status
                && f.Files.GetLatest("attacker", "colony-a")?.Identity.SnapshotId == (change == "removed" ? null
                    : change == "snapshot" ? "newer-attacker" : f.Original.Envelope.Identity.SnapshotId),
                "Rejected cleanup changed a competing snapshot or support event.");
        }
        GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Download recovery leaked the mutation gate.");
    }
}
