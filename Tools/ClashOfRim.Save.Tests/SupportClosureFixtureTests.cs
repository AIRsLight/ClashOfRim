using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using static SupportClosureTestData;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class SupportClosureFixtureTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        TestModuleDependencies.RequireIndependent(typeof(SupportClosureTests), typeof(SupportClosureRecoveryPreparationTests),
            typeof(SupportClosureRecoveryFixture), typeof(SupportClosureTestClient), typeof(SupportClosureTestData),
            typeof(SupportClosureTestAssertions), typeof(SessionTestAuthentication), typeof(SqliteTestDatabase),
            typeof(SqliteTestDirectory), typeof(EventLedgerTestEvidence), typeof(SnapshotRecoveryTestStore), typeof(SnapshotPublicationProbe));
        SnapshotEndpointTestClient.LoadLocalization();
        VerifyClientOwnership();
        foreach (string kind in Kinds)
        foreach (bool prepared in new[] { false, true })
        foreach (bool fail in new[] { false, true }) VerifyLifetime(factory, kind, prepared, fail);
    }

    private static void VerifyClientOwnership()
    {
        var state = new ClashOfRimNetworkState(); state.Ledger.Append(Source());
        var first = SupportClosureTestClient.Create(state);
        var tokens = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => first.Token("userA"))))
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(tokens.Distinct().Count() == 1, "Concurrent support client calls issued competing sessions.");
        var replacement = SupportClosureTestClient.Create(state);
        string replacementToken = replacement.Token("userA");
        Check(replacementToken != tokens[0] && !first.Call("return").Accepted && state.Ledger.ListAll().Count == 1,
            "Support client shared credentials with another instance or silently renewed its replaced session.");
        var independentState = new ClashOfRimNetworkState(); independentState.Ledger.Append(Source());
        var independent = SupportClosureTestClient.Create(independentState);
        Check(independent.Call("loss").Created && state.Ledger.ListAll().Count == 1 && replacement.Call("return").Created,
            "Support clients shared case state or invalidated another case's credentials.");
        SupportClosureTestAssertions.Closed(state.Ledger.ListAll(), "return");
        SupportClosureTestAssertions.Closed(independentState.Ledger.ListAll(), "loss");
    }

    private static void VerifyLifetime(Func<string, SaveSnapshotPackage> factory, string kind, bool prepared, bool fail)
    {
        string? root = null;
        void Run() => SupportClosureRecoveryFixture.With(factory, kind, f =>
        {
            root = Path.GetDirectoryName(f.Database)!;
            Check(f.Kind == kind && f.State.Ledger.ListAll().Count == 1 && !f.State.SnapshotCommits.HasPending && f.Store.Writes == 0
                && f.Client.Token("userA") == f.AuthToken, "Support fixture inherited another case's state or credentials.");
            f.Seed(prepared);
            string evidence = f.Evidence();
            var oldState = f.State; var oldStore = f.Store; var oldClient = f.Client;
            var original = f.Source; var gift = f.Gift; var package = f.Package; string oldToken = f.AuthToken;
            oldState.LoginSessions.EndUser("userA");
            Check(!oldClient.Call(kind).Accepted && evidence == f.Evidence(), "Expired support fixture client recovered pending business.");
            oldStore.BeforePrepare = oldStore.AfterRead = () => throw new InjectedFailure();
            oldStore.BeforeRead = _ => throw new InjectedFailure();
            oldStore.BeforePublication = oldStore.AfterPublication = _ => throw new InjectedFailure();
            oldStore.FailPreparation = oldStore.FailPublication = oldStore.FailAfterPublication = true;
            oldStore.ReadOverride = package;
            f.AfterPublication = () => throw new InjectedFailure();
            f.Open();
            Check(!ReferenceEquals(oldState, f.State) && !ReferenceEquals(oldStore, f.Store) && !ReferenceEquals(oldClient, f.Client)
                && ReferenceEquals(original, f.Source) && ReferenceEquals(gift, f.Gift) && ReferenceEquals(package, f.Package)
                && ReferenceEquals(f.Store.Gate, f.State.RaidSettlementSnapshotMutationGate) && !ReferenceEquals(oldStore.Gate, f.Store.Gate)
                && f.AuthToken != oldToken && f.Client.Token("userA") == f.AuthToken && f.AfterPublication is null
                && f.Store.BeforePrepare is null && f.Store.BeforeRead is null && f.Store.AfterRead is null
                && f.Store.BeforePublication is null && f.Store.AfterPublication is null && f.Store.ReadOverride is null
                && !f.Store.FailPreparation && !f.Store.FailPublication && !f.Store.FailAfterPublication
                && f.Store.Writes == 0 && f.Store.WriteAttempts == 0 && f.Store.Preparations == 0 && f.Store.WritesByOwner.Count == 0
                && f.State.SnapshotCommits.HasPending && evidence == f.Evidence(),
                "Support reopening lost durable inputs or retained old clients, locks, credentials or faults.");
            Check(!f.Client.Call(kind, token: oldToken, authenticate: false).Accepted && evidence == f.Evidence(),
                "Reopened support fixture accepted an old credential before recovering business.");
            if (fail) throw new InjectedFailure();

            var reply = f.Call();
            Check(reply.Accepted && reply.Created && reply.ReturnId is not null && !f.State.SnapshotCommits.HasPending
                && f.Store.Writes == 1 && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
                && Same(f.Gift, f.State.Ledger.FindByIdempotencyKey("closure-foreign")!)
                && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token",
                "Reopened support fixture lost its original intent, return receipt or single publication.");
            f.AssertClosed(kind);
            string events = Snapshot(f.State.Ledger), snapshots = JsonSerializer.Serialize(f.Store.ListLatest());
            var retry = f.Call();
            Check(retry.Accepted && !retry.Created && retry.ReturnId == reply.ReturnId && events == Snapshot(f.State.Ledger)
                && snapshots == JsonSerializer.Serialize(f.Store.ListLatest()) && f.Store.Writes == 1,
                "Support fixture retry changed durable business or repeated recovery.");
            f.AssertReleased();
        });
        if (fail) Expect<InjectedFailure>(Run, "Support fixture swallowed a failing case."); else Run();
        Check(root is not null && !Directory.Exists(root), "Support fixture leaked its database, snapshots or pending journal.");
    }

    private sealed class InjectedFailure : Exception;
}
