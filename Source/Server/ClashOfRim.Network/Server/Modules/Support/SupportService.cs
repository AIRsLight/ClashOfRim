using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportReturns;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportClosures;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

internal sealed class SupportService(IAuthoritativeEventLedger ledger, SnapshotRequestAuthorization requests,
    OnlinePresenceRegistry presence, SupportClosures closures)
{
    internal FinishSupportPawnResponse FinishSupportPawn(FinishSupportPawnRequest request)
    {
        using (var recovery = requests.EnterColony(request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return new FinishSupportPawnResponse(
                    failure, request.EventId, null, null, false);
            return FinishSupportPawnLocked(request);
        }
    }

    private FinishSupportPawnResponse FinishSupportPawnLocked(FinishSupportPawnRequest request)
    {
        ProtocolResponse? validation = ValidateFinishSupportPawnRequest(request);
        if (validation is not null)
        {
            return new FinishSupportPawnResponse(validation, request.EventId, null, null, created: false);
        }

        AuthoritativeEvent? supportEvent = ledger.Find(request.EventId);
        if (supportEvent is null
            || supportEvent.Type != ServerEventType.SupportPawn
            || supportEvent.Payload is not SupportPawnEventPayload payload
            || !EventVisibility.MatchesParty(supportEvent.Target, request.UserId, request.ColonyId))
        {
            return new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Support.NotFoundOrInvisible")),
                request.EventId,
                null,
                null,
                created: false);
        }

        if (payload.ReturnToSender || request.PawnGlobalKey != payload.PawnGlobalKey)
            return new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.ReferenceMismatch")), request.EventId, null, null, false);

        if (closures.Find(supportEvent) is { } existing)
        {
            closures.Commit(supportEvent, existing);
            return new FinishSupportPawnResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Support.AlreadyEnded")),
                supportEvent.EventId, existing.Event.EventId, null, false);
        }
        if (supportEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
            return new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Support.AlreadyEnded")), supportEvent.EventId, null, null, false);
        PawnExchangePackage? pawnPackage = null;
        if (!request.PawnDead)
        {
            if (!TryToPawnExchangePackage(request.PawnPackage, out pawnPackage, out string pawnPackageFailure)
                || pawnPackage is null)
            {
                return new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.FinishPawnPackageInvalid", ("MESSAGE", pawnPackageFailure))),
                    request.EventId,
                    null,
                    null,
                    created: false);
            }
            if (pawnPackage.Reference.GlobalId != payload.PawnGlobalKey)
                return new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.PackageReferenceMismatch")),
                    request.EventId, null, null, false);

            try
            {
                _ = SafePawnExchangeSerializer.Serialize(pawnPackage);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.FinishPawnPackageInvalid", ("MESSAGE", ex.Message))),
                    request.EventId,
                    null,
                    null,
                    created: false);
            }
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        bool ownerOnline = presence.IsUserOnline(supportEvent.Actor.UserId);
        AuthoritativeEvent finishEvent = request.PawnDead
            ? CreateSupportPawnLossReturnEvent(supportEvent, payload, request.PawnName, request.FinishReason, ownerOnline, nowUtc)
            : CreateSupportPawnFinishReturnEvent(supportEvent, payload, request, pawnPackage, ownerOnline, nowUtc);
        bool created = closures.Commit(supportEvent,
            new SupportClosure(finishEvent, request.PawnDead ? SupportClosureKind.Loss : SupportClosureKind.Return));
        return new FinishSupportPawnResponse(
            ProtocolResponse.Ok(request.PawnDead ? ServerText.Format("Support.Dead") : ServerText.Format("Support.Finished")),
            supportEvent.EventId, finishEvent.EventId, null, created);
    }
    internal RejectSupportPawnResponse RejectSupportPawn(RejectSupportPawnRequest request)
    {
        using (var recovery = requests.EnterColony(request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return new RejectSupportPawnResponse(
                    failure, request.EventId, null, false);
            return RejectSupportPawnLocked(request);
        }
    }

    private RejectSupportPawnResponse RejectSupportPawnLocked(RejectSupportPawnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.RejectMissingFields")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false);
        }

        AuthoritativeEvent? supportEvent = ledger.Find(request.EventId);
        if (supportEvent is null || !EventVisibility.MatchesParty(supportEvent.Target, request.UserId, request.ColonyId))
        {
            return new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Support.NotFoundOrInvisible")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false);
        }

        if (supportEvent.Type != ServerEventType.SupportPawn || supportEvent.Payload is not SupportPawnEventPayload supportPayload)
        {
            return new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Support.RejectSupportOnly")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false);
        }

        if (supportEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget || !supportPayload.TemporaryControl)
        {
            return new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Support.NotRejectable")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false);
        }

        if (closures.Find(supportEvent) is { } existing)
        {
            if (existing.Kind != SupportClosureKind.Rejection)
                return new RejectSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Support.AlreadyEnded")), request.EventId, null, false);
            closures.Commit(supportEvent, existing);
            return new RejectSupportPawnResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Support.RejectedDuplicate")),
                supportEvent.EventId, existing.Event.EventId, false);
        }
        if (supportEvent.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.Failed)
            return new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Support.AlreadyAppliedCannotReject")), request.EventId, null, false);

        // A legacy MarkRejected may have committed without its return event. Keep its original decision.
        DateTimeOffset nowUtc = supportEvent.Status == ServerEventStatus.RejectedByTarget
            ? supportEvent.DecisionAtUtc ?? throw new InvalidOperationException("Rejected support has no original decision time.")
            : DateTimeOffset.UtcNow;
        string? reason = supportEvent.Status == ServerEventStatus.RejectedByTarget ? supportEvent.DecisionReason : request.Reason;
        AuthoritativeEvent rejected = new LedgerRejectionChange(supportEvent, nowUtc, reason).Updated;
        AuthoritativeEvent returnEvent = CreateRejectedSupportPawnReturnEvent(rejected, supportPayload, reason,
            presence.IsUserOnline(supportEvent.Actor.UserId), nowUtc);
        bool created = closures.Commit(supportEvent, new SupportClosure(returnEvent, SupportClosureKind.Rejection));
        return new RejectSupportPawnResponse(ProtocolResponse.Ok(ServerText.Format("Support.Rejected")), supportEvent.EventId, returnEvent.EventId, created);
    }
    private static ProtocolResponse? ValidateFinishSupportPawnRequest(FinishSupportPawnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
            || string.IsNullOrWhiteSpace(request.FinishReason)
            || string.IsNullOrWhiteSpace(request.PawnGlobalKey))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Support.FinishMissingFields"));
        }

        return null;
    }

}
