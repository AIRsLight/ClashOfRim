using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

// Operates on the ledger selected by snapshot planning, including its staging overlay.
internal static class SupportApplicationConfirmation
{
    internal static ConfirmEventApplicationResultDto Confirm(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        AuthoritativeEvent supportEvent,
        SupportPawnEventPayload supportPayload,
        IAuthoritativeEventLedger ledger,
        DateTimeOffset nowUtc)
    {
        if (!EventVisibility.MatchesParty(supportEvent.Target, userId, colonyId))
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Support.EventNotFoundOrInvisible")),
                appliedSnapshotId: null,
                "TargetIdentityMismatch");
        }

        if (supportEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            AuthoritativeEvent repaired = SnapshotApplicationCommit.Apply(ledger, supportEvent,
                baseSnapshotId, acceptedSnapshot.Identity.SnapshotId!, nowUtc);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Support.ArrivalAlreadyConfirmed")),
                repaired.AppliedSnapshotId,
                "AlreadyApplied");
        }

        if (supportEvent.Status is ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed)
        {
            ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.NeedsManualReview,
                ServerText.Format("Support.ArrivalAlreadyEnded"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Support.ArrivalAlreadyEnded")),
                appliedSnapshotId: null,
                supportEvent.Status.ToString());
        }

        SnapshotIdentity identity = acceptedSnapshot.Identity;
        if (!string.Equals(identity.OwnerId, userId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, colonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                ServerText.Format("Support.ArrivalSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.IdentityMismatch, ServerText.Format("Support.ArrivalSnapshotIdentityMismatch")),
                appliedSnapshotId: null,
                "SnapshotIdentityMismatch");
        }

        if (!IsSupportPawnAppliedClientResult(application.ClientApplicationResult, supportPayload))
        {
            ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.NeedsManualReview,
                ServerText.Format("Support.ArrivalClientResultMissing"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Support.ArrivalClientResultMissing")),
                appliedSnapshotId: null,
                "NotArrived");
        }

        AuthoritativeEvent applied = SnapshotApplicationCommit.Apply(ledger, supportEvent,
            baseSnapshotId, identity.SnapshotId!, nowUtc);

        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ProtocolResponse.Ok(supportPayload.ReturnToSender ? ServerText.Format("Support.ReturnSnapshotConfirmed") : ServerText.Format("Support.ArrivalSnapshotConfirmed")),
            applied.AppliedSnapshotId,
            supportPayload.ReturnToSender ? "ReturnToSenderApplied" : "Arrived");
    }

    private static bool IsSupportPawnAppliedClientResult(
        string clientApplicationResult,
        SupportPawnEventPayload supportPayload)
    {
        if (supportPayload.ReturnToSender)
        {
            return string.Equals(clientApplicationResult, "SupportPawnReturnCaravanCreated", StringComparison.Ordinal);
        }

        return string.Equals(clientApplicationResult, "SupportPawnArrived", StringComparison.Ordinal);
    }

}
