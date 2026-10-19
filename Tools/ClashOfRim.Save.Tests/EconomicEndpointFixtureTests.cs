using AIRsLight.ClashOfRim.Save;
using ClashOfRim.TestSupport;
using static SnapshotTestAssertions;

internal static class EconomicEndpointFixtureTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        VerifyDependencies();
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

    private static void VerifyDependencies()
    {
        Type Root(Type type) => type.DeclaringType is { } parent ? Root(parent) : type;
        Type[] roots = [typeof(EconomicEndpointFixture), typeof(SnapshotRecoveryTestStore),
            typeof(SnapshotReplayRace), typeof(SnapshotEndpointPreparation), typeof(SnapshotTestAssertions),
            typeof(EconomicEndpointRecoveryPreparationTests), typeof(EconomicRequestBindingTests)];
        var assembly = typeof(EconomicEndpointFixture).Assembly;
        foreach (var root in roots)
        {
            var violations = ModuleDependencyGuard.FindViolations(assembly.GetTypes().Where(type => Root(type) == root),
                dependency => Root(dependency) is var owner && owner.Assembly == assembly
                    && owner != root && owner.Name.EndsWith("Tests", StringComparison.Ordinal));
            Check(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }
    }

    private sealed class InjectedFailure : Exception;
}
