using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static SupportClosureTestData;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class SupportClosureRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in Kinds)
        {
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
                "target", "cancelled", "delivery", "closure", "opposite", "published-revoke", "published-replace" })
                SupportClosureRecoveryFixture.With(factory, kind, f => VerifyClosureRecovery(f, change));
            foreach (bool revoke in new[] { false, true })
                SupportClosureRecoveryFixture.With(factory, kind, f => VerifyClosureRecoveryReplay(f, revoke));
            foreach (string bad in new[] { "invalid", "body", "owner" })
                SupportClosureRecoveryFixture.With(factory, kind, f => VerifyClosureRecoveryIdentity(f, bad));
            SupportClosureRecoveryFixture.With(factory, kind, f => VerifyConcurrentClosureRecovery(f));
            SupportClosureRecoveryFixture.With(factory, kind, f => VerifyPreparedClosureRecovery(f));
        }
    }

    private static void VerifyClosureRecovery(SupportClosureRecoveryFixture f, string change)
    {
        f.Seed();
        (bool Accepted, bool Created, string? ReturnId) reply = default;
        Exception? failure = null; string? before = null;
        AuthoritativeEvent? competingReturn = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 1, "Support closure published before journal preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, OriginalTime);
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "target") SqliteTestDatabase.Execute(f.Database, "update events set target_user_id = 'other' where event_id = 'source';");
                if (change == "cancelled") f.State.Ledger.ChangeStatus("source", ServerEventStatus.Cancelled);
                if (change == "delivery") f.State.Ledger.MarkDelivered("source", "delivered-during-recovery", OriginalTime);
                if (change is "closure" or "opposite")
                {
                    string competingKind = change == "closure" ? f.Kind : f.Kind == "return" ? "loss" : "return";
                    var closed = f.Client.Call(competingKind, token: f.AuthToken, authenticate: false);
                    Check(closed.Accepted && closed.Created, "Could not close support while recovery was preparing.");
                    competingReturn = f.State.Ledger.Find(closed.ReturnId!)!;
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser("userA");
                    else { f.State.LoginSessions.EndUser("userA"); f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Check((failure is not null) == failed, "Unexpected support recovery outcome: " + f.Kind + "/" + change + " " + failure);
        if (denied || failed)
        {
            Check(!reply.Accepted && reply.ReturnId is null && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
                "Denied or failed support closure changed durable state or discarded recovery.");
            if (change == "failure")
            {
                f.Open(); f.State.SnapshotMutations.Recover();
                Check(f.Call().Created, "Support closure did not resume after reopening failed preparation.");
                f.AssertClosed(f.Kind);
            }
        }
        else
        {
            Check(!f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && Same(f.Gift, f.State.Ledger.FindByIdempotencyKey("closure-foreign")!), "Support closure did not recover the original intent once.");
            bool rejected = change is "target" or "cancelled" or "published-revoke" or "published-replace" || change == "opposite" && f.Kind == "reject";
            Check(reply.Accepted != rejected, "Support closure ignored current authority or business state: " + f.Kind + "/" + change);
            if (rejected)
            {
                Check(!reply.Created && reply.ReturnId is null, "Rejected support closure exposed a return receipt.");
                if (competingReturn is null) Check(f.State.Ledger.ListAll().Count == 2 && f.State.EventNotifications.GetVersion("owner") == 0,
                    "Rejected support closure created a return or notified its owner.");
                if (change.StartsWith("published")) Check(Same(f.Source, f.State.Ledger.Find("source")!), "Recovery logout changed the support source.");
            }
            else if (competingReturn is null) { Check(reply.Created, "New support closure was not created."); f.AssertClosed(f.Kind); }
            else Check(!reply.Created && reply.ReturnId == competingReturn.EventId, "Competing closure did not retain its original acknowledgement.");
            if (competingReturn is not null) Check(Same(competingReturn, f.State.Ledger.Find(competingReturn.EventId)!), "Recovery replaced the winning closure.");
            if (change == "delivery") Check(f.State.Ledger.Find("source")!.DeliveredToSnapshotId == "delivered-during-recovery", "Recovery discarded delivery metadata.");
            if (change.StartsWith("published"))
            {
                f.Open();
                Check(f.Call().Created && f.Store.Writes == 0 && Same(f.Gift, f.State.Ledger.FindByIdempotencyKey("closure-foreign")!),
                    "A fresh session did not resume support closure without republishing recovered business.");
                f.AssertClosed(f.Kind);
            }
        }
        f.AssertReleased();
    }

    private static void VerifyClosureRecoveryReplay(SupportClosureRecoveryFixture f, bool revoke)
    {
        var first = f.Call(); Check(first.Created, "Could not seed a support replay.");
        string events = JsonSerializer.Serialize(f.State.Ledger.ListAll());
        f.Seed(); (bool Accepted, bool Created, string? ReturnId) reply = default;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser("userA"); });
        Check(reply.Accepted != revoke && !reply.Created && reply.ReturnId == (revoke ? null : first.ReturnId)
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke,
            "Support replay recovered or returned a receipt after revocation.");
        Check(JsonSerializer.Serialize(f.State.Ledger.ListAll().Where(e => e.EventId != f.Gift.EventId).ToArray()) == events,
            "Support replay rewrote the source or return.");
        f.AssertReleased();
    }

    private static void VerifyClosureRecoveryIdentity(SupportClosureRecoveryFixture f, string bad)
    {
        f.Seed(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized support request prepared recovery.");
        var reply = f.Client.Call(f.Kind, user: bad == "body" ? "intruder" : "userA",
            token: bad == "owner" ? f.Client.Token("owner", "home") : bad == "invalid" ? "invalid" : f.AuthToken, authenticate: false);
        Check(!reply.Accepted && reply.ReturnId is null && before == f.Evidence() && f.Store.Writes == 0, "Wrong support principal recovered pending business.");
        f.AssertReleased();
    }

    private static void VerifyConcurrentClosureRecovery(SupportClosureRecoveryFixture f)
    {
        f.Seed();
        var replies = SnapshotReplayRace.Run(_ => Task.FromResult(f.Call()), () => { });
        Check(replies.All(r => r.Accepted) && replies.Count(r => r.Created) == 1 && replies.Select(r => r.ReturnId).Distinct().Count() == 1
            && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending, "Concurrent support recovery duplicated publication or closures.");
        f.AssertClosed(f.Kind); f.AssertReleased();
    }

    private static void VerifyPreparedClosureRecovery(SupportClosureRecoveryFixture f)
    {
        f.Seed(prepared: true);
        f.Store.BeforePrepare = () => throw new Exception("Cached recovery payload was prepared again.");
        f.AfterPublication = () => f.State.AuthTokens.RevokeForUser("userA");
        var reply = f.Call();
        Check(!reply.Accepted && reply.ReturnId is null && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending
            && Same(f.Source, f.State.Ledger.Find("source")!) && f.State.Ledger.ListAll().Count == 2,
            "Already prepared recovery skipped its final authority check.");
        f.AssertReleased();
    }
}
