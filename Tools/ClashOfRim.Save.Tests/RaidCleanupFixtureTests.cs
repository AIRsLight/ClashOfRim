using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SnapshotTestAssertions;

internal static class RaidCleanupFixtureTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        TestModuleDependencies.RequireIndependent(typeof(RaidCleanupCommitTests), typeof(SnapshotDownloadAuthorizationTests),
            typeof(DownloadCleanupJournalRecoveryTests), typeof(RaidCleanupFixture), typeof(SnapshotDownloadTestClient),
            typeof(RaidCleanupSnapshot), typeof(SnapshotRecoveryTestStore), typeof(SessionTestAuthentication),
            typeof(SnapshotTestAssertions), typeof(SqliteTestDirectory));
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool timeout in new[] { false, true })
        foreach (bool own in new[] { false, true })
        foreach (bool fail in new[] { false, true }) VerifyLifetime(original, timeout, own, fail);
    }

    private static void VerifyLifetime(SaveSnapshotPackage original, bool timeout, bool own, bool fail)
    {
        string? root = null;
        void Run() => RaidCleanupFixture.With(original, timeout, f =>
        {
            root = Path.GetDirectoryName(f.Journal)!;
            Check(!f.State.SnapshotCommits.HasPending && f.State.Ledger.ListAll().Count == 2,
                "Cleanup fixture inherited another case's journal or losses.");
            f.PrepareDownloadAccess(allied: !own);
            f.SeedDownloadRecovery(f.DownloadForeignIntent(), own);
            var persisted = JsonNode.Parse(f.DownloadRecoveryEvidence())!;
            // The stored index omits parsed Things and the source path; use its actual persisted view across a reopen.
            persisted["snapshots"] = JsonSerializer.SerializeToNode(new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")).ListLatest());
            var state = f.State; var store = f.Store; var cleaned = f.Cleaned; var mutation = f.Mutation;
            store.BeforePrepare = store.AfterRead = () => throw new Exception("Old cleanup hook survived reopening.");
            store.FailPreparation = store.FailAfterPublication = true;
            store.ReadOverride = cleaned;
            f.Open();
            string evidence = f.DownloadRecoveryEvidence();
            Check(!ReferenceEquals(state, f.State) && !ReferenceEquals(store, f.Store) && ReferenceEquals(f.Files, f.Store.Inner)
                && ReferenceEquals(f.Original, original) && ReferenceEquals(f.Cleaned, cleaned) && ReferenceEquals(f.Mutation, mutation)
                && f.DownloadReader == "attacker" && JsonNode.DeepEquals(persisted, JsonNode.Parse(evidence)) && f.State.SnapshotCommits.HasPending
                && f.Store.BeforePrepare is null && f.Store.AfterRead is null && f.Store.ReadOverride is null
                && !f.Store.FailPreparation && !f.Store.FailPublication && !f.Store.FailAfterPublication
                && f.Store.WriteAttempts == 0 && f.Store.PreparedWriteAttempts == 0 && f.Store.WritesByOwner.Count == 0,
                "Cleanup reopening changed its captured input or retained stale runtime, faults or publication counters.");
            Check(f.Download(false) is { Accepted: false, SnapshotId: null, Lineage: null, Recovery: null }
                && evidence == f.DownloadRecoveryEvidence(), "Reopened cleanup fixture reused download credentials or recovered before authentication.");
            if (fail) throw new InjectedFailure();

            f.PrepareDownloadAccess(allied: !own);
            f.State.SnapshotMutations.Recover();
            f.InvokeCleanup();
            Check(f.Download(false).Accepted && !f.State.SnapshotCommits.HasPending
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == 1
                && f.Store.WritesByOwner.GetValueOrDefault("foreign") == (own ? 0 : 1)
                && f.State.Ledger.ListAll().Count == (own ? 4 : 5)
                && f.State.Ledger.Find(f.Support.EventId)!.Status == ServerEventStatus.Failed,
                "Reopened cleanup fixture lost its transaction, fresh download authorization or one-time losses.");
            GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Cleanup fixture leaked its mutation gate.");
        });
        if (fail) Expect<InjectedFailure>(Run, "Cleanup fixture swallowed a failing case.");
        else Run();
        Check(root is not null && !Directory.Exists(root), "Cleanup fixture leaked its database, snapshots or journal.");
    }

    private sealed class InjectedFailure : Exception;
}
