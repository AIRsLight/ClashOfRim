using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class EconomicEndpointFixtureTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        TestModuleDependencies.RequireIndependent(typeof(EconomicEndpointFixture), typeof(SnapshotRecoveryTestStore),
            typeof(SnapshotReplayRace), typeof(SnapshotEndpointPreparation), typeof(SnapshotTestAssertions),
            typeof(SqliteTestDirectory), typeof(EconomicEndpointRecoveryPreparationTests), typeof(EconomicRequestBindingTests));
        SnapshotEndpointTestClient.LoadLocalization();
        string? previousRoot = null;
        foreach (bool fail in new[] { false, true })
        {
            string? root = null;
            bool failed = false;
            try
            {
                EconomicEndpointFixture.With(factory, "gift", fixture =>
                {
                    root = Path.GetDirectoryName(fixture.Journal)!;
                    Check(root != previousRoot && fixture.State.Ledger.ListAll().Count == 0
                        && !fixture.State.SnapshotCommits.HasPending, "Economic fixture inherited another case's state.");
                    fixture.Seed();
                    Check(Directory.GetFiles(fixture.Journal, "*.json").Length == 1,
                        "Fixture cleanup check requires a durable interrupted commit.");
                    if (fail) throw new InjectedFailure();
                });
            }
            catch (InjectedFailure) { failed = true; }
            Check(failed == fail && root is not null && !Directory.Exists(root),
                "Economic fixture swallowed a test failure or leaked its database, snapshots or pending journal.");
            previousRoot = root;
        }
    }

    private sealed class InjectedFailure : Exception;
}
