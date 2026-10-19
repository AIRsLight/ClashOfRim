using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class RaidCreationFixtureTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        TestModuleDependencies.RequireIndependent(typeof(RaidCommitCrashTests), typeof(RaidCreationRequestBindingTests),
            typeof(RaidCreationFixture), typeof(RaidCreationTestClient), typeof(RaidTestPreparation), typeof(PendingBankTestDebt),
            typeof(SnapshotEndpointPreparation), typeof(SnapshotRecoveryTestStore), typeof(SessionTestAuthentication), typeof(SqliteTestDirectory));
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool guarded in new[] { false, true })
        foreach (bool fail in new[] { false, true }) VerifyLifetime(factory, guarded, fail);
    }

    private static void VerifyLifetime(Func<string, SaveSnapshotPackage> factory, bool guarded, bool fail)
    {
        string? root = null;
        void Run() => RaidCreationFixture.With(factory, guarded, f =>
        {
            root = Path.GetDirectoryName(f.Journal)!;
            Check(f.State.Ledger.ListAll().Count == 0 && !f.State.SnapshotCommits.HasPending,
                "Raid fixture inherited another case's events or intent.");
            var package = f.Package;
            var intent = f.Seed();
            var oldState = f.State;
            var oldStore = f.Store;
            oldState.LoginSessions.EndUser("userA");
            oldStore.BeforePrepare = () => throw new Exception("Previous fixture's preparation hook survived reopening.");
            oldStore.FailPreparation = true;
            oldStore.FailPublication = true;
            f.Open();
            Check(!ReferenceEquals(f.State, oldState) && !ReferenceEquals(f.Store, oldStore) && ReferenceEquals(f.Package, package)
                && f.Guarded == guarded && ReferenceEquals(f.Store.Gate, f.State.RaidSettlementSnapshotMutationGate)
                && f.Store.BeforePrepare is null && !f.Store.FailPreparation && !f.Store.FailPublication && f.Store.Writes == 0,
                "Raid reopening replaced its input or retained another runtime's gate, fault or counters.");
            Check(f.State.SnapshotCommits.HasPending && Directory.GetFiles(f.Journal, "*.json").Length == 1
                && f.Store.GetLatest("userA", "colonyA")?.Identity.SnapshotId == "before"
                && f.Store.GetLatest("userB", "colonyB")?.Identity.SnapshotId == "defender"
                && f.State.MercenaryGuards.FindByIdempotencyKey("guard") == intent.Raid!.Guard,
                "Raid reopening lost its durable intent, snapshots or original guard.");
            if (fail) throw new InjectedFailure();

            var recovered = f.Call();
            Check(recovered.Result.Accepted && recovered.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && recovered.EventId == intent.Raid!.Raid.EventId
                && recovered.AppliedSnapshotId == intent.Package.Envelope.Identity.SnapshotId
                && recovered.NextLineageToken == intent.Package.Envelope.NextLineageToken
                && !f.State.SnapshotCommits.HasPending && f.Store.Writes == 1 && f.Store.PreparedWrites == 1
                && f.State.Ledger.ListAll().Count == (guarded ? 2 : 1)
                && (!guarded || f.State.MercenaryGuards.FindByIdempotencyKey("guard")!.Status == MercenaryGuardContractRegistry.StatusConsumed),
                "Reopened raid fixture did not authenticate and recover its original transaction once.");
        });
        if (fail) Expect<InjectedFailure>(Run, "Raid fixture swallowed a failing case.");
        else Run();
        Check(root is not null && !Directory.Exists(root), "Raid fixture leaked its database, snapshots or pending journal.");
    }

    private sealed class InjectedFailure : Exception;
}
