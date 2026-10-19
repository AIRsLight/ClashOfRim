using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using RecoveryFixture = EventFailureRecoveryFixture;
using static EventFailureGraph;
using static SnapshotTestAssertions;

internal static class EventFailureRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in Kinds)
        {
            foreach (string invalid in new[] { "token", "body", "fields", "fields-valid" }) RecoveryFixture.With(factory, kind, f => VerifyRecoveryIdentity(f, invalid));
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "failure", "newer", "compete",
                "published-revoke", "published-replace", "target", "terminal", "report", "delivery" })
                RecoveryFixture.With(factory, kind, f => VerifyRecovery(f, change));
            foreach (bool revoke in new[] { false, true }) RecoveryFixture.With(factory, kind, f => VerifyRecoveryReplay(f, revoke));
            RecoveryFixture.With(factory, kind, f =>
            {
                f.SeedIntent();
                var replies = SnapshotReplayRace.Run(_ => Task.FromResult(f.Call()), () => { });
                Check(replies.All(r => r.Result.Accepted) && replies.Count(r => r.AffectedEventCount > 0) == 1,
                    "Concurrent failure reports recalculated the failure batch.");
                f.AssertRecovered(); f.Graph.AssertCompleted(f.State.Ledger); f.AssertReleased();
            });
            RecoveryFixture.With(factory, kind, f =>
            {
                f.SeedIntent(prepared: true);
                f.Store.BeforePrepare = () => throw new Exception("Cached failure report recovery payload was prepared again.");
                f.AfterPublication = () => f.State.AuthTokens.RevokeForUser(f.AuthUser);
                var reply = f.Call();
                Check(!reply.Result.Accepted && reply.AffectedEventCount == 0 && reply.TerminalStatus == "" && f.Business() == f.InitialBusiness,
                    "Cached recovery skipped failure report authorization."); f.AssertRecovered(); f.AssertReleased();
            });
        }
        foreach (string change in new[] { "none", "actor", "published-actor", "logout" })
            RecoveryFixture.With(factory, "loss", f => VerifyDelegatedRecovery(f, change), delegated: true);
    }

    private static void VerifyRecovery(RecoveryFixture f, string change)
    {
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null; Exception? failure = null; string? before = null, business = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0 && f.Business() == f.InitialBusiness, "Failure report published before recovery preparation.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser(f.AuthUser);
                if (change == "replace") f.State.LoginSessions.TryCreate(f.AuthUser, f.AuthUser + "-colony", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser(f.AuthUser);
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, f.Package.Index, Original);
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "target") f.ChangeParty("target_user_id");
                if (change == "terminal") f.State.Ledger.ChangeStatus(f.Source.EventId, ServerEventStatus.AppliedToSnapshot);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Source.EventId, "during-recovery", Original);
                if (change == "report")
                {
                    Check(f.Call().AffectedEventCount > 0, "Could not report failure during recovery preparation.");
                    foreach (var notice in f.State.Ledger.ListByType(ServerEventType.ServerNotification))
                    { f.State.Ledger.MarkDelivered(notice.EventId, "before", Original); f.State.Ledger.MarkApplied(notice.EventId, "consumed", Original); }
                }
                if (change.StartsWith("published")) f.AfterPublication = () =>
                {
                    if (change == "published-revoke") f.State.AuthTokens.RevokeForUser(f.AuthUser);
                    else { f.State.LoginSessions.EndUser(f.AuthUser); f.State.LoginSessions.TryCreate(f.AuthUser, f.AuthUser + "-colony", DateTimeOffset.UtcNow, out _); }
                };
                before = f.Evidence(); business = f.Business();
            });
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "failure" or "newer";
        Check((failure is not null) == failed, "Unexpected failure report recovery: " + f.Kind + "/" + change + " " + failure);
        if (denied || failed)
        {
            Check(reply?.Result.Accepted != true && (reply is null || reply.AffectedEventCount == 0 && reply.TerminalStatus == "")
                && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending,
                "Rejected failure report changed durable state or returned success fields.");
            if (change == "failure")
            { f.Open(); Check(f.Call().Result.Accepted, "Failure report did not resume after reopening."); f.AssertRecovered(); f.Graph.AssertCompleted(f.State.Ledger); }
        }
        else
        {
            f.AssertRecovered();
            bool rejected = change is "target" or "terminal" or "published-revoke" or "published-replace";
            Check(reply is not null && reply.Result.Accepted != rejected, "Failure report ignored current authority or source: " + f.Kind + "/" + change);
            if (rejected)
            {
                Check(reply!.AffectedEventCount == 0 && f.Business() == business && f.Notifications() == "0/0/0",
                    "Rejected failure report changed its graph or notifications.");
                if (change.StartsWith("published"))
                {
                    Check(reply.TerminalStatus == "", "Revoked failure report exposed the original result.");
                    f.Open(); Check(f.Call().Result.Accepted && f.Store.Writes == 0, "Fresh failure report session republished recovered business.");
                    f.Graph.AssertCompleted(f.State.Ledger);
                }
            }
            else
            {
                f.Graph.AssertCompleted(f.State.Ledger);
                if (change == "report") Check(reply!.AffectedEventCount == 0 && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                    && f.Business() == business, "Repeated failure recalculated refunds or reset consumed notifications.");
                else Check(reply!.AffectedEventCount > 0, "Failure report did not commit its batch.");
                if (change == "delivery") Check(f.State.Ledger.Find(f.Source.EventId)!.DeliveredToSnapshotId == "during-recovery", "Failure report discarded current delivery metadata.");
            }
        }
        f.AssertReleased();
    }

    private static void VerifyRecoveryReplay(RecoveryFixture f, bool revoke)
    {
        Check(f.Call().Result.Accepted, "Could not seed failure report replay."); string before = f.Business();
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { if (revoke) f.State.AuthTokens.RevokeForUser(f.AuthUser); });
        Check(reply!.Result.Accepted != revoke && reply.AffectedEventCount == 0 && f.Business() == before
            && f.Store.Writes == (revoke ? 0 : 1) && f.State.SnapshotCommits.HasPending == revoke
            && (!revoke || reply.TerminalStatus == ""), "Failure replay recovered or exposed a result after revocation."); f.AssertReleased();
    }

    private static void VerifyRecoveryIdentity(RecoveryFixture f, string invalid)
    {
        f.SeedIntent(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Invalid failure report prepared recovery.");
        // Missing fields take precedence even when the token is invalid.
        var request = new ReportEventApplicationFailureRequest(invalid.StartsWith("fields") ? "" : "key", f.Source.EventId,
            invalid == "body" ? "intruder" : f.Source.Target.UserId, f.Source.Target.ColonyId!, "current", "reason", null,
            invalid is "token" or "fields" ? "invalid" : f.AuthToken);
        var reply = f.State.EventFailureReports.Report(request);
        Check(!reply.Result.Accepted && reply.AffectedEventCount == 0 && reply.TerminalStatus == ""
            && reply.Result.ErrorCode == (invalid.StartsWith("fields") ? ProtocolErrorCode.ValidationFailed
                : invalid == "token" ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.ServerRejected)
            && before == f.Evidence() && f.Store.Writes == 0,
            $"Invalid failure report changed its response or pending business: {f.Kind}/{invalid}; "
            + $"response={JsonSerializer.Serialize(reply)}, evidenceChanged={before != f.Evidence()}, writes={f.Store.Writes}."); f.AssertReleased();
    }

    private static void VerifyDelegatedRecovery(RecoveryFixture f, string change)
    {
        f.SeedIntent(); ReportEventApplicationFailureResponse? reply = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () =>
            {
                if (change == "actor") f.ChangeParty("actor_user_id");
                if (change == "published-actor") f.AfterPublication = () => f.ChangeParty("actor_user_id");
                if (change == "logout") f.State.LoginSessions.EndUser(f.AuthUser);
                before = f.Evidence();
            });
        Check(reply!.Result.Accepted == (change == "none"), "Failure recovery lost or retained the wrong event-based authorization.");
        if (change is "actor" or "logout") Check(f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending && before == f.Evidence(),
            "Revoked event grant published pending business.");
        else f.AssertRecovered();
        if (change == "none") f.Graph.AssertCompleted(f.State.Ledger);
        else Check(reply.TerminalStatus == "" && reply.AffectedEventCount == 0 && f.State.Ledger.Find(f.Source.EventId)!.Status == f.Source.Status
            && f.State.Ledger.ListByType(ServerEventType.ServerNotification).Count == 0, "Revoked event grant reported failure or exposed its result.");
        f.AssertReleased();
    }

}
