internal static class GiftRejectionFixtureTests
{
    public static void Run() => TestModuleDependencies.RequireIndependent(
        typeof(GiftRejectionEndpointTests), typeof(GiftRejectionRecoveryPreparationTests),
        typeof(GiftRejectionCase), typeof(GiftRejectionTestClient), typeof(GiftRejectionRecoveryFixture),
        typeof(SessionTestAuthentication), typeof(SnapshotMutationAuthorizationProbe),
        typeof(InterruptedEventLedger), typeof(EventLedgerTestEvidence), typeof(SqliteTestDirectory));
}
