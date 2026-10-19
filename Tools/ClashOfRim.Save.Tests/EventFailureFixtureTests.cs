internal static class EventFailureFixtureTests
{
    public static void Run() => TestModuleDependencies.RequireIndependent(
        typeof(EventFailureReportTests), typeof(EventFailureRecoveryPreparationTests),
        typeof(EventFailureGraph), typeof(EventFailureTestClient), typeof(EventFailureRecoveryFixture),
        typeof(EventLedgerTestEvidence), typeof(InterruptedEventLedger), typeof(SnapshotPublicationProbe),
        typeof(SqliteTestDirectory));
}
