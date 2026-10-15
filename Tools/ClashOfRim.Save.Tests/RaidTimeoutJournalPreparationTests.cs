using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static partial class RaidTimeoutRecoveryTests
{
    private static void RunJournalPreparation(SaveSnapshotPackage original)
    {
        foreach (string phase in new[] { "classify", "pending", "schedule-capture", "schedule-commit", "cleanup-capture", "cleanup-commit", "complete" })
        {
            foreach (string change in new[] { "none", "compete", "newer", "failure" })
                WithFixture(original, f => VerifyJournalPreparation(f, phase, change));
            if (phase is "schedule-commit" or "cleanup-capture" or "cleanup-commit" or "complete")
                foreach (string change in new[] { "source", "snapshot" })
                    WithFixture(original, f => VerifyJournalPreparation(f, phase, change));
            if (phase is "cleanup-capture" or "cleanup-commit")
                WithFixture(original, f => VerifyJournalPreparation(f, phase, "job"));
        }
    }

    private static void VerifyJournalPreparation(Fixture f, string phase, string change)
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
        if (phase.StartsWith("cleanup")) f.CallMaintenance("ScheduleOfflineTimedOutRaid", f.State, source, f.Now);
        var foreign = f.CreateForeignIntent();
        Action? recoveryHook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation()) f.SeedForeign(foreign);
            f.Store.BeforePrepare = () => recoveryHook?.Invoke();
        }
        if (phase is "schedule-commit") f.Artifacts.BeforePrepare = () => { f.Artifacts.BeforePrepare = null; Inject(); };
        else if (phase is "cleanup-commit") f.Store.BeforePrepare = () => { f.Store.BeforePrepare = null; Inject(); };
        else if (phase == "complete") f.Store.BeforeRead = owner => { if (owner == "later") { f.Store.BeforeRead = null; Inject(); } };
        else Inject();

        Exception? failure = null; string? before = null; int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => recoveryHook = hook, () =>
        {
            try
            {
                if (phase == "classify") f.Reconcile();
                else if (phase is "pending" or "complete") ClashOfRimNetworkServer.RecoverPendingRaidTimeoutWork(f.State, f.Now);
                else if (phase.StartsWith("schedule")) f.CallMaintenance("ScheduleOfflineTimedOutRaid", f.State, source, f.Now);
                else f.CallMaintenance("CleanupTimedOutRaidAttackerSnapshots", f.State, new[] { source }, f.Now);
            }
            catch (Exception ex) { failure = ex; }
        }, () =>
        {
            captures++;
            Require(f.Store.WritesByOwner.GetValueOrDefault("foreign") == 0 && f.State.Ledger.FindByIdempotencyKey("foreign") is null,
                "Maintenance recovery published before preparation completed.");
            if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
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
        Require(captures == 1, "Maintenance did not reach the expected recovery boundary: " + phase);
        bool failed = change is "newer" or "failure";
        Require((failure is not null) == failed, "Unexpected maintenance recovery result: " + phase + "/" + change + " " + failure);
        if (failed)
        {
            Require(before == f.JournalEvidence() && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 0 && f.State.SnapshotCommits.HasPending,
                "Failed maintenance recovery changed business or discarded its intent.");
            if (change == "failure")
            {
                f.Open(); ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                Require(!f.State.SnapshotCommits.HasPending && f.State.Ledger.FindByIdempotencyKey("foreign") is not null,
                    "Preparation failure could not resume after reopening.");
            }
        }
        else
        {
            Require(!f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
                && f.Files.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && JsonSerializer.Serialize(f.State.Ledger.FindByIdempotencyKey("foreign")) == JsonSerializer.Serialize(foreign.Event),
                "Maintenance did not publish the original foreign intent exactly once.");
            bool changed = change is "source" or "snapshot" or "job";
            var current = (RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload;
            Require(current.TimeoutWork!.Completed == (full && !changed), "Maintenance acknowledged a stale timeout decision: " + phase + "/" + change);
            if (changed && phase.StartsWith("cleanup")) Require(f.Store.WritesByOwner.GetValueOrDefault("attacker") == 0,
                "Cleanup published after source, snapshot or task changed during recovery.");
            if (changed && phase == "schedule-commit") Require(f.State.SnapshotPostUploadJobs.ListAll().Count == 0,
                "Scheduling published stale evidence after recovery.");
            if (!changed && phase.StartsWith("schedule")) Require(f.State.SnapshotPostUploadJobs.ListReady(f.Now).Count == 1
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == 0, "Evidence scheduling did not preserve its order before cleanup.");
        }
        Require(!Directory.EnumerateFiles(Path.Combine(f.Root, "artifacts"), "*.tmp").Any(), "Recovery leaked prepared evidence files.");
        Task.Run(() =>
        {
            Require(Monitor.TryEnter(f.State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Maintenance leaked its mutation gate.");
            Monitor.Exit(f.State.RaidSettlementSnapshotMutationGate);
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private sealed partial class Fixture
    {
        public object? CallMaintenance(string method, params object[] arguments)
        {
            try { return typeof(ClashOfRimNetworkServer).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        public (SaveSnapshotPackage Package, AuthoritativeEvent Event) CreateForeignIntent()
        {
            var template = Files.GetLatestPackage("defender", "defender-colony")!;
            var package = template with { Envelope = template.Envelope with { Identity = new("foreign", "foreign-colony", "foreign-snapshot"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign-snapshot")], "foreign"), Now);
            return (package, gift);
        }
        public void SeedForeign((SaveSnapshotPackage Package, AuthoritativeEvent Event) intent)
        {
            Store.Fault = "before";
            var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, Now);
            try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, intent.Package, snapshot, intent.Event, (_, _) => { }, _ => { }); }
            catch (IOException) { }
            finally { Store.Fault = null; }
            Require(State.SnapshotCommits.HasPending, "Could not seed unrelated recovery during maintenance.");
        }
        public string JournalEvidence() => JsonSerializer.Serialize(new { snapshots = Files.ListLatest(), events = State.Ledger.ListAll(),
            jobs = State.SnapshotPostUploadJobs.ListAll(), players = new[] { State.Players.FindByUserId("attacker"), State.Players.FindByUserId("foreign") },
            journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });
    }
}
