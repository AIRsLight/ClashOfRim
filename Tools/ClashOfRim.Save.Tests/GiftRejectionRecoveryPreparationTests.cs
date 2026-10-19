using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using RecoveryFixture = GiftRejectionRecoveryFixture;
using static GiftRejectionCase;
using static GiftRejectionTestClient;
using static SessionTestAuthentication;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class GiftRejectionRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
            "target", "cancelled", "applied", "failed", "delivery", "rejection", "published-revoke", "published-replace" })
            RecoveryFixture.With(factory, f => VerifyRecovery(f, change));
        foreach (bool revoked in new[] { false, true }) RecoveryFixture.With(factory, f => VerifyRecoveryReplay(f, revoked));
        foreach (string bad in new[] { "invalid", "body", "sender", "missing" }) RecoveryFixture.With(factory, f => VerifyRecoveryIdentity(f, bad));
        RecoveryFixture.With(factory, f =>
        {
            f.Seed();
            var replies = SnapshotReplayRace.Run(_ => Task.FromResult(f.Call()), () => { });
            Check(replies.All(r => r.Result.Accepted) && replies.Count(r => r.ReturnEventCreated) == 1
                && replies.Select(r => r.ReturnEventId).Distinct().Count() == 1 && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending,
                "Concurrent gift rejection duplicated recovery or returns.");
            f.Gift.AssertReturned(f.State.Ledger, 3); f.AssertReleased();
        });
        RecoveryFixture.With(factory, f =>
        {
            f.Seed(prepared: true);
            f.Store.BeforePrepare = () => throw new Exception("Cached gift recovery payload was prepared again.");
            f.AfterPublication = () => f.State.AuthTokens.RevokeForUser("receiver");
            var reply = f.Call();
            Check(!reply.Result.Accepted && reply.ReturnEventId is null && f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending
                && SameRecord(f.Source, f.State.Ledger.Find(f.Source.EventId)!) && f.State.Ledger.ListAll().Count == 2,
                "Cached recovery bypassed gift rejection authorization.");
            f.AssertReleased();
        });
    }

    private static void VerifyRecovery(RecoveryFixture f, string change)
    {
        f.Seed(); RejectGiftResponse? reply = null; Exception? failure = null; string? before = null;
        AuthoritativeEvent? competingReturn = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 1, "Gift rejection published before journal preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, At);
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "target")
                {
                    using var connection = new SqliteConnection("Data Source=" + f.Database); connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "update events set target_user_id = 'other' where event_id = $id";
                    command.Parameters.AddWithValue("$id", f.Source.EventId); command.ExecuteNonQuery();
                }
                if (change == "cancelled") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.Cancelled);
                if (change == "applied")
                {
                    f.State.Ledger.MarkDelivered(f.Source.EventId, "new-base", At);
                    f.State.Ledger.MarkApplied(f.Source.EventId, "new-base", At);
                }
                if (change == "failed") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.Failed);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Source.EventId, "during-recovery", At);
                if (change == "rejection")
                {
                    var result = f.Call(); Check(result.ReturnEventCreated, "Could not reject gift during preparation.");
                    competingReturn = f.State.Ledger.Find(result.ReturnEventId!)!;
                    f.State.Ledger.MarkDelivered(competingReturn.EventId, "return-base", At);
                    f.State.Ledger.MarkApplied(competingReturn.EventId, "home", At);
                    competingReturn = f.State.Ledger.Find(competingReturn.EventId)!;
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser("receiver");
                    else { f.State.LoginSessions.EndUser("receiver"); f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Check((failure is not null) == failed, "Unexpected gift recovery outcome: " + change + " " + failure);
        if (denied || failed)
        {
            Check(reply?.Result.Accepted != true && reply?.ReturnEventId is null && before == f.Evidence()
                && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Failed or denied rejection changed durable state.");
            if (change == "failure")
            {
                f.Open(); Check(f.Call().ReturnEventCreated, "Gift rejection did not resume after reopening failed preparation.");
                f.Gift.AssertReturned(f.State.Ledger, 3); Check(f.Store.Writes == 1 && !f.State.SnapshotCommits.HasPending, "Reopened gift recovery was incomplete.");
            }
        }
        else
        {
            Check(!f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && SameRecord(f.ForeignGift, f.State.Ledger.Find(f.ForeignGift.EventId)!), "Gift rejection did not recover the original intent once.");
            bool rejected = change is "target" or "cancelled" or "applied" or "failed" or "published-revoke" or "published-replace";
            Check(reply is not null && reply.Result.Accepted != rejected, "Gift rejection ignored current authority or source: " + change);
            if (rejected)
            {
                Check(!reply!.ReturnEventCreated && reply.ReturnEventId is null && f.State.Ledger.ListAll().Count == 2
                    && f.State.EventNotifications.GetVersion("sender") == 0, "Rejected request leaked a return or notification.");
                if (change.StartsWith("published"))
                {
                    Check(SameRecord(f.Source, f.State.Ledger.Find(f.Source.EventId)!), "Recovery logout changed the source gift.");
                    f.Open(); Check(f.Call().ReturnEventCreated && f.Store.Writes == 0, "Fresh session did not resume without republishing.");
                    f.Gift.AssertReturned(f.State.Ledger, 3);
                }
            }
            else if (competingReturn is null) { Check(reply!.ReturnEventCreated, "New gift return was not created."); f.Gift.AssertReturned(f.State.Ledger, 3); }
            else Check(!reply!.ReturnEventCreated && reply.ReturnEventId == competingReturn.EventId
                && SameRecord(competingReturn, f.State.Ledger.Find(competingReturn.EventId)!), "Recovery overwrote a consumed competing return.");
            if (change == "delivery") Check(f.State.Ledger.Find(f.Source.EventId)!.DeliveredToSnapshotId == "during-recovery", "Gift rejection lost delivery metadata.");
        }
        f.AssertReleased();
    }

    private static void VerifyRecoveryReplay(RecoveryFixture f, bool revoke)
    {
        var first = f.Call(); Check(first.ReturnEventCreated, "Could not seed gift replay.");
        string events = Serialize(f.State.Ledger); f.Seed(); RejectGiftResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser("receiver"); });
        Check(reply!.Result.Accepted != revoke && !reply.ReturnEventCreated && reply.ReturnEventId == (revoke ? null : first.ReturnEventId)
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke,
            "Gift replay recovered or exposed a receipt after revocation.");
        Check(JsonSerializer.Serialize(f.State.Ledger.ListAll().Where(e => e.EventId != f.ForeignGift.EventId).ToArray()) == events,
            "Gift replay rewrote the source or return."); f.AssertReleased();
    }

    private static void VerifyRecoveryIdentity(RecoveryFixture f, string bad)
    {
        f.Seed(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized or malformed gift request prepared recovery.");
        var request = Request(f.Source, bad == "invalid" ? "invalid" : bad == "sender" ? Token(f.State, "sender", "home") : f.AuthToken);
        if (bad == "body") request = new(f.Source.EventId, "intruder", "away", "base", "first", f.AuthToken);
        if (bad == "missing") request = new(f.Source.EventId, "receiver", "away", "", "first", f.AuthToken);
        var reply = Reject(f.State, request);
        Check(!reply.Result.Accepted && reply.ReturnEventId is null && before == f.Evidence() && f.Store.Writes == 0,
            "Invalid gift rejection recovered pending business."); f.AssertReleased();
    }

}
