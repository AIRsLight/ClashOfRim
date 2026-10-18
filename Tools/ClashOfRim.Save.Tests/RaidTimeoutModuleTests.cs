using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RaidTimeoutModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool online in new[] { false, true })
                    foreach (bool prepared in new[] { false, true }) TimeoutAndReplay(online, prepared);
            }
            MissingEvidenceRequiresReview();
            OfflineDecisionSurvivesReconnect();
            foreach (string change in new[] { "none", "denied", "newer" }) RevalidateDownload(change);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void TimeoutAndReplay(bool online, bool prepared)
    {
        using var f = new Fixture(prepared);
        var c = f.Context;
        using var presence = online ? f.Connect() : null;
        f.Configuration = new(raidMaxDuration: TimeSpan.FromHours(3));
        Check(f.Timeouts.Reconcile(f.Now).FailedRaids.Count == 0 && c.Jobs.ListAll().Count == 0,
            "Timeout ignored current duration configuration.");
        f.Configuration = new(raidMaxDuration: TimeSpan.FromHours(1), raidTimeoutGracePeriod: TimeSpan.FromMinutes(10));
        var result = f.Timeouts.Reconcile(f.Now);
        var source = c.Ledger.Find(c.Data.Raid.EventId)!;
        Check(result.FailedRaids.Count == 1 && source.Status == ServerEventStatus.Failed
            && ((RaidEventPayload)source.Payload).TimeoutWork is { Completed: true } work && work.AttackerWasOffline == !online,
            "Timeout classification or acknowledgement was lost: " + online + "/" + prepared);
        var cleaned = c.Snapshots.GetLatestPackage("attacker", "attacker-colony")!;
        Check(cleaned.Envelope.Identity.SnapshotId != "evidence" && cleaned.Envelope.PreviousSnapshotId == "evidence"
            && !cleaned.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
            && c.Players.FindByUserId("attacker")!.CurrentSnapshotId == cleaned.Envelope.Identity.SnapshotId
            && c.Snapshots.GetLatest("defender", "defender-colony")!.Identity.SnapshotId == "before",
            "Timeout cleanup lost lineage, left the battle behind or changed the defender before settlement.");
        Check(c.Jobs.ListAll().Count == (online ? 0 : 1), "Online/offline timeout queued the wrong settlement work.");
        if (!online)
        {
            var job = c.Jobs.ListAll().Single();
            var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
            Check(job.State == SnapshotPostUploadJobState.Ready && job.SnapshotId == "evidence"
                && payload.Origin == RaidSettlementOrigin.OfflineTimeout && payload.RequestHash is null
                && c.Artifacts.Read(payload.EvidenceArtifactId)!.Payload.AsSpan().SequenceEqual(f.Evidence.Payload),
                "Attacker cleanup discarded or replaced the original offline evidence.");
            Check(new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(Path.Combine(c.Root, "jobs.sqlite")))
                .Find(job.JobId) == job, "Offline evidence became ready without a durable task.");
        }
        string before = JsonSerializer.Serialize(c.Ledger.ListAll());
        long attackerNotifications = f.Notifications.GetVersion("attacker"), defenderNotifications = f.Notifications.GetVersion("defender");
        Check(attackerNotifications > 0 && defenderNotifications > 0, "Timeout parties were not notified.");
        f.Configuration = new(raidMaxDuration: TimeSpan.FromHours(10));
        f.Timeouts.Reconcile(f.Now.AddDays(1));
        f.Timeouts.RecoverPending(f.Now.AddDays(1));
        Check(JsonSerializer.Serialize(c.Ledger.ListAll()) == before
            && c.Snapshots.GetLatest("attacker", "attacker-colony")!.Envelope == cleaned.Envelope
            && f.Notifications.GetVersion("attacker") == attackerNotifications
            && f.Notifications.GetVersion("defender") == defenderNotifications,
            "Completed timeout repeated publication, notifications or classification after policy changed.");
    }

    private static void MissingEvidenceRequiresReview()
    {
        using var f = new Fixture(true);
        var c = f.Context;
        c.Snapshots.RemoveLatest("defender", "defender-colony");
        f.Timeouts.Reconcile(f.Now);
        Check(((RaidEventPayload)c.Ledger.Find(c.Data.Raid.EventId)!.Payload).TimeoutWork is { Completed: false }
            && c.Jobs.ListAll().Count == 0 && c.Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == "evidence",
            "Missing defender evidence permitted attacker cleanup or completed timeout work.");
        c.Snapshots.StoreLatest(c.Data.Original, c.Data.Original.Index, c.Data.Now);
        f.Timeouts.RecoverPending(f.Now);
        Check(c.Ledger.Find(c.Data.Raid.EventId)!.Status == ServerEventStatus.Conflict && c.Jobs.ListAll().Count == 0,
            "Restoring a missing snapshot silently bypassed manual review.");
        c.Ledger.ChangeStatus(c.Data.Raid.EventId, ServerEventStatus.Failed); // Explicitly resolve the review before retrying.
        f.Timeouts.RecoverPending(f.Now);
        Check(((RaidEventPayload)c.Ledger.Find(c.Data.Raid.EventId)!.Payload).TimeoutWork is { Completed: true }
            && c.Jobs.ListAll().Single().State == SnapshotPostUploadJobState.Ready
            && c.Artifacts.Read(c.ArtifactId)!.Payload.AsSpan().SequenceEqual(f.Evidence.Payload),
            "Pending offline work could not resume with its original evidence.");
    }

    private static void OfflineDecisionSurvivesReconnect()
    {
        using var f = new Fixture(true);
        var c = f.Context;
        RaidTimeoutProcessingResult result;
        using (c.Mutations.EnterUnrecovered()) result = f.Timeouts.ExpireUnderMutation(f.Now);
        Check(c.Recovery.Replays == 0 && c.Jobs.ListAll().Count == 0, "Locked timeout classification started snapshot preparation.");
        using var connection = f.Connect();
        f.Configuration = new(raidMaxDuration: TimeSpan.FromHours(10));
        f.Timeouts.CompletePending(result, f.Now);
        var work = ((RaidEventPayload)c.Ledger.Find(c.Data.Raid.EventId)!.Payload).TimeoutWork;
        Check(work is { AttackerWasOffline: true, Completed: true } && c.Jobs.ListAll().Count == 1,
            "Reconnect or policy change rewrote the durable offline decision.");
    }

    private static void RevalidateDownload(string change)
    {
        using var f = new Fixture(true);
        var c = f.Context;
        c.Ledger.ChangeStatus(c.Data.Raid.EventId, ServerEventStatus.AppliedToSnapshot);
        bool authorized = true;
        int captures = 0;
        c.Recovery.OnCapture = () => { if (++captures == 2) c.Recovery.NeedsPreparation = true; };
        SaveSnapshotPackage? reply = null;
        SnapshotPreparationGateProbe.Run(c.Gate, hook => c.Recovery.OnPrepare = hook,
            () => reply = f.Cleanup.BeforeDownload(f.Evidence, "attacker", "attacker-colony", f.Now,
                () => authorized ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, "revoked")), () =>
            {
                Check(c.Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == "evidence", "Download cleanup published before recovery preparation.");
                if (change == "denied") authorized = false;
                if (change == "newer")
                {
                    var newer = f.Evidence with { Envelope = f.Evidence.Envelope with { Identity = new("attacker", "attacker-colony", "newer") } };
                    c.Snapshots.StoreLatest(newer, newer.Index, f.Now);
                }
            });
        Check(c.Snapshots.GetLatest("attacker", "attacker-colony")!.Identity.SnapshotId == (change == "none"
                ? reply!.Envelope.Identity.SnapshotId : change == "newer" ? "newer" : "evidence")
            && (reply!.Envelope.Identity.SnapshotId != "evidence") == (change == "none") && c.Jobs.ListAll().Count == 0,
            "Download cleanup published after authority or destination changed.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RaidEvidenceModuleTestContext Context;
        internal readonly EventNotificationHub Notifications = new();
        private readonly OnlinePresenceRegistry presence = new();
        internal readonly RaidTimeouts Timeouts;
        internal readonly RaidBattleCleanup Cleanup;
        internal readonly SaveSnapshotPackage Evidence;
        internal ClashOfRimServerConfiguration Configuration = new(raidMaxDuration: TimeSpan.FromHours(1), raidTimeoutGracePeriod: TimeSpan.FromMinutes(10));
        internal DateTimeOffset Now => Context.Data.Now.AddHours(2);
        internal Fixture(bool prepared)
        {
            Context = new(prepared);
            var c = Context;
            Evidence = new(c.Request.Snapshot.Envelope, c.Request.EvidencePayload, c.Request.Snapshot.Index);
            c.Snapshots.StoreLatest(Evidence, Evidence.Index, c.Data.Now);
            c.Players.RecordLatestSnapshotReference("attacker", "attacker-colony", "evidence", c.Data.Now);
            var queries = new RaidQueries(c.Ledger, () => Configuration);
            var offline = new RaidOfflineEvidence(c.Ledger, c.Snapshots, c.Jobs, c.Artifacts, c.Mutations,
                queries, c.Scheduling, () => NullLogger.Instance);
            Cleanup = new(c.Ledger, c.Snapshots, c.Jobs, c.Mutations, queries, new SnapshotCommitService(),
                new BankLoanRegistry(), c.Players, presence, Notifications, () => NullLogger.Instance);
            Timeouts = new(c.Ledger, c.Snapshots, c.Jobs, c.Mutations, presence, Notifications, offline, Cleanup, () => Configuration);
        }
        internal OnlinePresenceLease Connect()
        {
            Check(presence.TryConnectExclusive("attacker", out var lease), "Could not connect attacker fixture.");
            return lease!;
        }
        public void Dispose() => Context.Dispose();
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
