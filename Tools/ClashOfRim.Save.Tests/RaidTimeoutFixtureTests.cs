using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SnapshotTestAssertions;

internal static class RaidTimeoutFixtureTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        TestModuleDependencies.RequireIndependent(typeof(RaidTimeoutRecoveryTests), typeof(RaidTimeoutJournalPreparationTests),
            typeof(RaidTimeoutFixture), typeof(RaidCleanupSnapshot), typeof(RaidCreationTestClient),
            typeof(SnapshotRecoveryTestStore), typeof(SnapshotArtifactTestStore), typeof(VersionedSnapshotArtifactTestStore),
            typeof(SnapshotArtifactStoreView), typeof(SqliteTestDirectory), typeof(SnapshotTestAssertions));
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool offline in new[] { false, true })
        foreach (bool own in new[] { false, true })
        foreach (bool fail in new[] { false, true }) VerifyLifetime(original, offline, own, fail);
        foreach (bool prepared in new[] { false, true }) VerifyHeldHandle(original, prepared);
    }

    private static void VerifyLifetime(SaveSnapshotPackage original, bool offline, bool own, bool fail)
    {
        string? root = null;
        void Run() => RaidTimeoutFixture.With(original, f =>
        {
            root = f.Root;
            Check(f.State.Ledger.ListAll().Count == 1 && !f.State.SnapshotCommits.HasPending && f.Store.Writes == 0,
                "Timeout fixture inherited another case's work or publications.");
            if (!offline) Check(f.State.OnlinePresence.TryConnectExclusive("attacker", out _), "Cannot seed fixture presence.");
            if (own)
            {
                f.Store.FailPublication = true;
                Expect<IOException>(f.Reconcile, "Could not interrupt fixture cleanup publication.");
                f.Store.FailPublication = false;
            }
            else
            {
                RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => !offline);
                if (offline) f.State.RaidOfflineEvidence.Schedule(f.State.Ledger.Find(f.Raid.EventId)!, f.Now);
                using (f.State.EnterSnapshotMutation()) f.SeedForeign(f.CreateForeignIntent());
            }
            Check(f.State.SnapshotCommits.HasPending && RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 1
                && f.State.SnapshotPostUploadJobs.ListAll().Count == (offline ? 1 : 0), "Timeout fixture did not retain its pending decision and evidence.");
            var persisted = JsonNode.Parse(f.JournalEvidence())!;
            // Compare the durable index, which intentionally omits parsed Things and the original source path.
            persisted["snapshots"] = JsonSerializer.SerializeToNode(new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")).ListLatest());
            var oldState = f.State; var oldStore = f.Store; var oldArtifacts = f.Artifacts; var raid = f.Raid; var now = f.Now;
            oldStore.BeforePrepare = oldStore.AfterRead = () => throw new InjectedFailure();
            oldStore.BeforeRead = _ => throw new InjectedFailure();
            oldStore.BeforePublication = oldStore.AfterPublication = _ => throw new InjectedFailure();
            oldStore.FailPreparation = oldStore.FailPublication = oldStore.FailAfterPublication = true;
            oldStore.ReadOverride = original;
            oldArtifacts.BeforeRead = oldArtifacts.AfterRead = oldArtifacts.AfterPrepare = () => throw new InjectedFailure();
            f.Open();
            Check(!ReferenceEquals(oldState, f.State) && !ReferenceEquals(oldStore, f.Store) && !ReferenceEquals(oldArtifacts, f.Artifacts)
                && ReferenceEquals(f.Files, f.Store.Inner) && ReferenceEquals(f.Original, original) && ReferenceEquals(f.Raid, raid) && f.Now == now
                && f.State.OnlinePresence.ListOnlineUsers().Count == 0 && f.Store.BeforePrepare is null && f.Store.BeforeRead is null
                && f.Store.AfterRead is null && f.Store.BeforePublication is null && f.Store.AfterPublication is null && f.Store.ReadOverride is null
                && !f.Store.FailPreparation && !f.Store.FailPublication && !f.Store.FailAfterPublication && f.Store.WriteAttempts == 0
                && f.Store.Writes == 0 && f.Store.Preparations == 0 && f.Store.WritesByOwner.Count == 0
                && f.Artifacts.BeforeRead is null && f.Artifacts.AfterRead is null && f.Artifacts.AfterPrepare is null && f.Artifacts.ActiveHandles == 0
                && JsonNode.DeepEquals(persisted, JsonNode.Parse(f.JournalEvidence())),
                "Timeout reopening changed durable decisions or retained stale runtime, presence, faults or counters.");
            if (offline)
            {
                var payload = RaidSettlementDeferredPayload.Deserialize(f.State.SnapshotPostUploadJobs.ListAll().Single().PayloadJson);
                Check(f.Artifacts.Read(payload.EvidenceArtifactId)!.Payload.SequenceEqual(original.Payload), "Timeout reopening lost its pre-cleanup evidence.");
            }
            if (fail) throw new InjectedFailure();

            // Presence changes after reopening must not reclassify the persisted timeout decision.
            if (offline) Check(f.State.OnlinePresence.TryConnectExclusive("attacker", out _), "Cannot reverse fixture presence.");
            f.Reconcile();
            var completed = (RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload;
            Check(completed.TimeoutWork is { Completed: true } work && work.AttackerWasOffline == offline && work.TimedOutAtUtc == now
                && !f.State.SnapshotCommits.HasPending && RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == 1 && f.Store.WritesByOwner.GetValueOrDefault("foreign") == (own ? 0 : 1)
                && f.State.Ledger.ListAll().Count(e => e.Type == ServerEventType.ServerNotification) == 2
                && f.State.Ledger.ListAll().Count(e => e.Payload is RaidEventPayload { AttackerLoss: not null }) == 1
                && f.State.SnapshotPostUploadJobs.ListAll().Count == (offline ? 1 : 0)
                && !f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"),
                "Reopened timeout lost its original classification, single cleanup, notifications or evidence job.");
            string evidence = f.JournalEvidence();
            f.Reconcile();
            Check(evidence == f.JournalEvidence() && f.Store.Writes == (own ? 1 : 2), "Repeated fixture reconciliation duplicated durable work.");
            GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Timeout fixture leaked its mutation gate.");
        });
        if (fail) Expect<InjectedFailure>(Run, "Timeout fixture swallowed a failing case."); else Run();
        Check(root is not null && !Directory.Exists(root), "Timeout fixture leaked its database, artifacts or pending journal.");
    }

    private static void VerifyHeldHandle(SaveSnapshotPackage original, bool prepared)
        => RaidTimeoutFixture.With(original, f =>
        {
            var artifacts = f.Artifacts; var state = f.State; var store = f.Store;
            if (!prepared) artifacts.Store("held", original);
            using (IDisposable handle = prepared ? artifacts.Prepare("held", original) : artifacts.ReadCaptured("held"))
            {
                string? error = null;
                try { f.Open(); } catch (Exception ex) { error = ex.Message; }
                Check(error == "Timeout fixture leaked artifact revision handles." && ReferenceEquals(artifacts, f.Artifacts)
                    && ReferenceEquals(state, f.State) && ReferenceEquals(store, f.Store), "Timeout reopening replaced an owned artifact handle.");
            }
            f.Open();
            Check(!ReferenceEquals(artifacts, f.Artifacts) && f.Artifacts.ActiveHandles == 0
                && !Directory.EnumerateFiles(Path.Combine(f.Root, "artifacts"), "*.tmp").Any()
                && f.Artifacts.Exists("held") != prepared, "Timeout reopening lost published evidence or retained an unpublished artifact.");
        });

    private sealed class InjectedFailure : Exception;
}
