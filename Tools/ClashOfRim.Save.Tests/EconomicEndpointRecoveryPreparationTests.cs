using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using Fixture = EconomicEndpointFixture;
using Reply = EconomicEndpointFixture.Reply;
using static SnapshotTestAssertions;

internal static class EconomicEndpointRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string change in new[] { "normal-move", "forced-none", "forced-move", "forced-remove", "forced-peace", "forced-pvp" })
            Fixture.With(factory, "gift", f => VerifyGiftTarget(f, change));
        foreach (string kind in new[] { "gift", "order", "self-delivery", "drop-pod" })
        {
            foreach (bool final in new[] { false, true })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "newer", "compete", "failure", "revoke-failure" })
                Fixture.With(factory, kind, f => VerifyRecovery(f, final, change));
            Fixture.With(factory, kind, VerifyConcurrent);
            Fixture.With(factory, kind, VerifyPrincipal);
            foreach (string change in kind == "gift" ? new[] { "disabled" } : kind == "order"
                ? new[] { "disabled", "fee", "limit" } : kind == "self-delivery" ? new[] { "cancel", "memo" } : new[] { "cancel" })
                Fixture.With(factory, kind, f => VerifyFinalValidation(f, change));
        }
    }

    private static void VerifyGiftTarget(Fixture f, string change)
    {
        f.ForcedGift = change.StartsWith("forced");
        f.State.UpdateServerConfiguration(new(pvpEnabled: true));
        f.State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
        f.SetTarget("Map_old"); f.Store.Reset();
        Reply? reply = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook, () => reply = f.Call(), () =>
        {
            if (change.EndsWith("-move")) f.SetTarget("Map_new");
            if (change.EndsWith("remove")) f.Store.Inner.RemoveLatest("userB", "colonyB");
            if (change.EndsWith("peace")) f.State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationNeutral, null, DateTimeOffset.UtcNow);
            if (change.EndsWith("pvp")) f.State.UpdateServerConfiguration(new(pvpEnabled: false));
            before = f.Evidence();
        });
        bool accepted = change.EndsWith("-move") || change.EndsWith("none");
        Check(reply!.Result.Accepted == accepted, "Gift target revalidation failed: " + change + " " + reply.Result.Message);
        if (accepted) Check(f.Store.Writes == 1 && f.State.Ledger.Find(reply.Id!)?.TargetContext?.MapUniqueId
            == (change.EndsWith("-move") ? "Map_new" : "Map_old"), "Gift retained its stale target map.");
        else Check(reply.Id is null && reply.Token is null && before == f.Evidence() && f.Store.Writes == 0, "Rejected forced gift changed state.");
        f.AssertReleased();
    }

    private static void VerifyRecovery(Fixture f, bool final, string change)
    {
        // Capture a real HTTP intent; final-boundary cases inject that exact business decision during upload preparation.
        var original = f.Seed();
        if (final) File.Delete(Directory.GetFiles(f.Journal, "*.json").Single());
        f.Open(); f.Store.Reset();
        Reply? reply = null; Exception? failure = null; string? before = null; int prepared = 0;
        void Change()
        {
            prepared++;
            if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
            if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
            if (change == "newer")
            {
                var newer = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer" } };
                f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (change == "compete") f.State.SnapshotMutations.Recover();
            if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
            before = f.Evidence();
        }
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0, "Recovery published during preparation.");
                if (!final) Change();
                else
                {
                    f.SeedDirect(original); f.Store.Reset();
                    f.Store.BeforePrepare = () =>
                    {
                        f.Store.BeforePrepare = null;
                        Check(!Monitor.IsEntered(f.Gate), "Final recovery prepared under the mutation gate.");
                        Task.Run(() => { using (f.State.EnterSnapshotMutation()) Change(); }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    };
                }
            });
        Check(prepared == 1, "Economic endpoint did not prepare recovery at its commit boundary: " + f.Kind);
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "newer" or "failure";
        Check((failure is not null) == failed, "Unexpected recovery failure: " + f.Kind + "/" + final + "/" + change + " " + failure);
        if (denied) Check(reply is { Result.Accepted: false, Id: null, Snapshot: null, Token: null, Owner: null, Buyer: null }
            && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Revoked request recovered or disclosed economic state.");
        else if (failed) Check(before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Failed recovery changed economic state.");
        else
        {
            f.AssertReplay(reply!, original);
            Check(f.Store.Writes == 1 && f.Store.PreparedWrites == (change == "compete" ? 0 : 1), "Recovery did not reuse prepared publication.");
        }
        f.AssertReleased();
    }

    private static void VerifyConcurrent(Fixture f)
    {
        var original = f.Seed(); f.Open(); f.Store.Reset();
        var replies = SnapshotReplayRace.Run(_ => Task.FromResult(f.Call()), () => { });
        foreach (var reply in replies) f.AssertReplay(reply, original);
        Check(f.Store.Writes == 1 && f.Store.PreparedWrites == 1, "Concurrent recovery published more than once.");
        f.AssertReleased();
    }

    private static void VerifyPrincipal(Fixture f)
    {
        f.Seed(); f.Open(); f.Store.Reset(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Mismatched principal started recovery.");
        Check(!f.Call(wrongPrincipal: true).Result.Accepted && before == f.Evidence() && f.Store.Writes == 0,
            "Mismatched principal changed economic state.");
        f.AssertReleased();
    }

    private static void VerifyFinalValidation(Fixture f, string change)
    {
        f.Store.Reset(); string? before = null; Reply? reply = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { f.ChangeTerms(change); before = f.Evidence(); });
        Check(reply is { Result.Accepted: false, Id: null, Snapshot: null, Token: null }
            && before == f.Evidence() && f.Store.Writes == 0 && !f.State.SnapshotCommits.HasPending,
            "Economic commit accepted changed terms: " + f.Kind + "/" + change);
        f.AssertReleased();
    }

}
