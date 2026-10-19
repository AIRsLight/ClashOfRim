using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SnapshotTestAssertions;

internal static class RaidEvidenceFixtureTests
{
    private const string HandleLeak = "Online request leaked artifact revision handles.";

    public static void Run()
    {
        TestModuleDependencies.RequireIndependent(typeof(RaidEvidenceAuthorizationTests), typeof(RaidEvidenceOnlineTests),
            typeof(RaidEvidenceRequestBindingTests), typeof(RaidEvidenceJournalRecoveryTests), typeof(RaidEvidenceFixture),
            typeof(RaidEvidenceTestClient), typeof(RaidEvidenceBindingSnapshot), typeof(SnapshotArtifactStoreView),
            typeof(VersionedSnapshotArtifactTestStore), typeof(SnapshotArtifactTestStore), typeof(SnapshotRecoveryTestStore),
            typeof(SnapshotPublicationProbe), typeof(SessionTestAuthentication), typeof(SqliteTestDirectory));
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool legacy in new[] { false, true })
        foreach (bool fail in new[] { false, true }) VerifyLifetime(legacy, fail);
        VerifyHeldHandleBlocksReopen();
        foreach (bool fail in new[] { false, true }) VerifyScopeChecksHandles(fail);
    }

    private static void VerifyLifetime(bool legacy, bool fail)
    {
        string? root = null;
        void Run() => RaidEvidenceFixture.With(RaidEvidenceBindingSnapshot.Create, f =>
        {
            root = Path.GetDirectoryName(f.RecoveryJournal)!;
            Check(!f.State.SnapshotCommits.HasPending && f.State.SnapshotPostUploadJobs.ListAll().Count == 0
                && f.Store.Writes == 0, "Evidence fixture inherited another case's publication or journal.");
            var first = f.Invoke();
            Check(first.Result.Accepted, "Could not seed evidence fixture lifecycle.");
            var intent = f.ForeignIntent();
            using (f.State.EnterSnapshotMutation()) f.SeedRecovery(intent);
            var persisted = JsonNode.Parse(f.RecoveryEvidence())!;
            // The file index intentionally omits parsed Things and the source path.
            var stateEvidence = JsonNode.Parse(persisted["state"]!.GetValue<string>())!;
            stateEvidence["snapshots"] = JsonSerializer.SerializeToNode(new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")).ListLatest());
            persisted["state"] = stateEvidence.ToJsonString();

            var oldState = f.State; var oldStore = f.Store; var oldArtifacts = f.ArtifactStore;
            var oldFaults = f.Publication; var package = f.Package; var raid = f.Raid;
            oldState.LoginSessions.EndUser("userA");
            string? authenticationFailure = null;
            try { f.Invoke(); } catch (Exception ex) { authenticationFailure = ex.Message; }
            Check(authenticationFailure == "Fixture authentication failed.", "Ended fixture session unexpectedly remained authorized.");
            oldStore.BeforePrepare = oldStore.BeforeRead = oldStore.AfterRead = () => throw new InjectedFailure();
            oldStore.FailPreparation = oldStore.FailPublication = oldStore.FailAfterPublication = true;
            oldStore.ReadOverride = package;
            oldFaults.PreparedFault = "before"; oldFaults.FailRaw = true;
            oldArtifacts.BeforeRead = oldArtifacts.AfterRead = oldArtifacts.AfterPrepare = () => throw new InjectedFailure();
            var stages = new List<string>();
            f.BeforeSchedule = _ => stages.Add("before"); f.AfterSchedule = _ => stages.Add("after");
            var beforeSchedule = f.BeforeSchedule; var afterSchedule = f.AfterSchedule;
            f.Open();
            var reopened = JsonNode.Parse(f.RecoveryEvidence())!;
            Check(JsonNode.DeepEquals(JsonNode.Parse(persisted["state"]!.GetValue<string>()), JsonNode.Parse(reopened["state"]!.GetValue<string>()))
                && JsonNode.DeepEquals(persisted["journal"], reopened["journal"]), "Evidence reopening lost persisted jobs, artifacts, snapshots or journal.");
            Check(!ReferenceEquals(oldState, f.State) && !ReferenceEquals(oldStore, f.Store) && !ReferenceEquals(oldArtifacts, f.ArtifactStore)
                && !ReferenceEquals(oldFaults, f.Publication) && ReferenceEquals(package, f.Package) && ReferenceEquals(raid, f.Raid)
                && f.BeforeSchedule == beforeSchedule && f.AfterSchedule == afterSchedule
                && f.Store.BeforePrepare is null && f.Store.BeforeRead is null && f.Store.AfterRead is null && f.Store.ReadOverride is null
                && !f.Store.FailPreparation && !f.Store.FailPublication && !f.Store.FailAfterPublication
                && f.Publication.PreparedFault is null && !f.Publication.FailRaw && f.Store.WriteAttempts == 0 && f.Store.Preparations == 0
                && f.Store.Writes == 0 && f.Store.WritesByOwner.Count == 0 && f.ArtifactStore.ActiveHandles == 0
                && f.ArtifactStore.BeforeRead is null && f.ArtifactStore.AfterRead is null && f.ArtifactStore.AfterPrepare is null,
                "Evidence reopening retained old runtime faults or discarded case inputs and pipeline observers.");
            // An old store must retain its own fault owner after the fixture opens a new one.
            Expect<IOException>(() => oldStore.BeforePublication!(true), "Old publication hook followed the replacement fixture state.");
            if (fail) throw new InjectedFailure();

            var reply = f.Invoke();
            Check(reply.Result.Accepted && reply.AppliedSnapshotId == first.AppliedSnapshotId && reply.NextLineageToken == first.NextLineageToken
                && !f.State.SnapshotCommits.HasPending && f.State.SnapshotPostUploadJobs.ListAll().Count == 1
                && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1 && f.Store.WritesByOwner.GetValueOrDefault("userA") == 0
                && JsonSerializer.Serialize(f.State.Ledger.FindByIdempotencyKey("foreign-gift")) == JsonSerializer.Serialize(intent.Event)
                && stages.SequenceEqual(new[] { "before", "after" }), "Reopened evidence lost fresh authorization, recovery, lineage or pipeline order.");
            string evidence = f.RecoveryEvidence();
            Check(f.Invoke().NextLineageToken == reply.NextLineageToken && evidence == f.RecoveryEvidence() && f.Store.Writes == 1,
                "Reopened evidence retry repeated durable publication.");
            f.AssertRecoveryReleased();
        }, persistent: true, legacyArtifacts: legacy);
        if (fail) Expect<InjectedFailure>(Run, "Evidence fixture swallowed a failing case."); else Run();
        Check(root is not null && !Directory.Exists(root), "Evidence fixture leaked its database, artifacts or pending journal.");
    }

    private static void VerifyHeldHandleBlocksReopen()
        => RaidEvidenceFixture.With(RaidEvidenceBindingSnapshot.Create, f =>
        {
            f.ArtifactStore.Store("held", f.Package);
            var store = f.ArtifactStore; var state = f.State;
            using (var handle = store.ReadCaptured("held"))
            {
                string? error = null;
                try { f.Open(); } catch (Exception ex) { error = ex.Message; }
                Check(error == HandleLeak && ReferenceEquals(store, f.ArtifactStore) && ReferenceEquals(state, f.State) && handle.IsCurrent,
                    "Evidence reopening replaced a store with an outstanding artifact handle.");
            }
            f.Open();
            Check(!ReferenceEquals(store, f.ArtifactStore) && f.ArtifactStore.ActiveHandles == 0 && f.ArtifactStore.Exists("held"),
                "Evidence reopening after handle disposal lost its durable artifact.");
        }, persistent: true);

    private static void VerifyScopeChecksHandles(bool fail)
    {
        string? root = null, error = null;
        ISnapshotPostUploadArtifactRead? handle = null;
        try
        {
            RaidEvidenceFixture.With(RaidEvidenceBindingSnapshot.Create, f =>
            {
                root = Path.GetDirectoryName(f.RecoveryJournal)!;
                f.ArtifactStore.Store("held", f.Package);
                handle = f.ArtifactStore.ReadCaptured("held");
                if (fail) throw new InjectedFailure();
            }, persistent: true);
        }
        catch (Exception ex) { error = ex.Message; }
        finally { handle?.Dispose(); }
        Check(error == HandleLeak && root is not null && !Directory.Exists(root),
            "Evidence scope missed an artifact leak or skipped cleanup on " + (fail ? "exceptional" : "normal") + " exit.");
    }

    private sealed class InjectedFailure : Exception;
}
