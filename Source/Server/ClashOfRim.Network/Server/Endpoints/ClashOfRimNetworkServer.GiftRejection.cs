using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult RejectGift(RejectGiftRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolErrorCode code, string message) => Results.Ok(new RejectGiftResponse(
            ProtocolResponse.Reject(code, message), request.EventId, null, false));
        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
            return Reject(ProtocolErrorCode.ValidationFailed, T("Gift.RejectMissingFields"));
        // Rejection belongs to the recipient's own colony; remote raid/scout grants do not authorize it.
        if (!IsAuthorizedForColony(state, request.AuthToken, request.UserId, request.ColonyId,
                authorizationEventId: null, authorizationScope: null, DateTimeOffset.UtcNow, out string failure))
            return Reject(ProtocolErrorCode.ServerRejected, failure);

        using (state.EnterSnapshotMutation())
        {
            RecoverSnapshotCommits(state);
            var gift = state.Ledger.Find(request.EventId);
            if (gift is null || !IsVisibleParty(gift.Target, request.UserId, request.ColonyId))
                return Reject(ProtocolErrorCode.EventNotFound, T("Gift.NotFoundOrInvisible"));
            if (gift.Type != ServerEventType.ItemDelivery || gift.Payload is not ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.Gift })
                return Reject(ProtocolErrorCode.ValidationFailed, T("Gift.RejectGiftOnly"));
            if (gift.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
                return Reject(ProtocolErrorCode.ServerRejected, T("Gift.NotRejectable"));
            if (gift.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.Failed)
                return Reject(ProtocolErrorCode.Conflict, T("Gift.NotRejectable"));

            GiftReturnResult result;
            try
            {
                // Replays run the same source/return validation and preserve the original decision.
                result = state.Ledger.RejectGiftAndCreateReturn(gift.EventId, DateTimeOffset.UtcNow, request.Reason,
                    state.OnlinePresence.IsUserOnline(gift.Actor.UserId));
            }
            catch (InvalidOperationException)
            {
                return Reject(ProtocolErrorCode.Conflict, T("Gift.NotRejectable"));
            }
            // Repeat the wake after a committed-but-unacknowledged attempt, even for consumed returns.
            state.EventNotifications.SignalUsers([result.RejectedGift.Target.UserId, result.ReturnEvent.Target.UserId]);
            return Results.Ok(new RejectGiftResponse(result.ReturnEventCreated
                ? ProtocolResponse.Ok(T("Gift.RejectedReturnCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Gift.RejectedDuplicate")),
                result.RejectedGift.EventId, result.ReturnEvent.EventId, result.ReturnEventCreated));
        }
    }
}
