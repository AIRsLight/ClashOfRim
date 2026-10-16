using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CompleteTradeOrder(CloseTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? AuthorizeAndValidate()
        {
            string failure = T("Auth.IdentityDenied");
            if (request.Owner is null || !IsAuthorizedForColony(state, request.AuthToken, request.Owner.UserId,
                    request.Owner.ColonyId ?? string.Empty, null, null, DateTimeOffset.UtcNow, out failure))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
            // Invalid legacy completion requests must not trigger journal recovery.
            return ValidateTradeCloseRequest(request);
        }
        using (var mutation = EnterRecoveredSnapshotMutation(state, AuthorizeAndValidate))
        {
            IResult Reject(ProtocolErrorCode code, string message) => Results.Ok(new CloseTradeOrderResponse(
                ProtocolResponse.Reject(code, message), request.TradeEventId, string.Empty, 0));
            if (mutation.Rejection is { } rejection)
                return Results.Ok(new CloseTradeOrderResponse(rejection, request.TradeEventId, string.Empty, 0));
            var order = state.Ledger.Find(request.TradeEventId);
            if (order?.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
                || !IsVisibleParty(order.Actor, request.Owner.UserId, request.Owner.ColonyId!))
                return Reject(ProtocolErrorCode.EventNotFound, T("Trade.CloseNotOwner"));
            // Completion belongs to the snapshot fulfillment transaction. This legacy request cannot spend escrow.
            if (!TryGetCompletedTradeParticipants(state.Ledger, order, out var participants))
                return Reject(ProtocolErrorCode.Conflict, "Trade completion requires a committed snapshot fulfillment and matching delivery evidence.");
            state.EventNotifications.SignalUsers(participants.Append(order.Actor.UserId));
            return Results.Ok(new CloseTradeOrderResponse(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest,
                T("Trade.Completed")), order.EventId, order.Status.ToString(), participants.Count));
        }
    }

    private static bool TryGetCompletedTradeParticipants(IAuthoritativeEventLedger ledger, AuthoritativeEvent order, out IReadOnlyList<string> participants)
    {
        participants = [];
        if (order.Status != ServerEventStatus.AppliedToSnapshot || order.Payload is not TradeEventPayload original) return false;
        var related = ledger.ListAll().Where(item => item.Payload is TradeEventPayload p && p.TradeId == order.EventId).ToArray();
        var exchanges = related.Where(item => item.Payload is TradeEventPayload
            { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange }).ToArray();
        if (exchanges.Length != 1) return false;
        var exchange = exchanges[0]; var payload = (TradeEventPayload)exchange.Payload;
        bool dropPod = payload.Stage == TradeStage.ServerDropPodExchange;
        if (exchange.Type != ServerEventType.Trade || exchange.Status != ServerEventStatus.AppliedToSnapshot
            || string.IsNullOrWhiteSpace(exchange.AppliedSnapshotId) || exchange.AppliedAtUtc is null
            || string.IsNullOrWhiteSpace(exchange.DeliveredToSnapshotId) || exchange.DeliveredAtUtc is null
            || exchange.LastApplicationResult != EventApplicationResultKind.Applied
            || exchange.Target != order.Actor || exchange.Actor.UserId == order.Actor.UserId
            || string.IsNullOrWhiteSpace(exchange.Actor.UserId) || string.IsNullOrWhiteSpace(exchange.Actor.ColonyId)
            || payload.AcceptedByUserId != exchange.Actor.UserId || payload.FeeSilver != original.FeeSilver
            || payload.FulfillmentMode != (dropPod ? TradeFulfillmentMode.ServerDropPod : TradeFulfillmentMode.SelfDelivery)
            || original.FulfillmentMode != TradeFulfillmentMode.Unspecified && original.FulfillmentMode != payload.FulfillmentMode
            || payload.PostagePaidByAcceptor != dropPod || !SameTradeEvidence(payload.OfferedItems, original.OfferedItems)
            || !SameTradeEvidence(exchange.TargetContext, order.TargetContext)
            || payload.RequestedItems.Any(item => item.SourceSnapshotId != exchange.AppliedSnapshotId)) return false;

        var memos = related.Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo }).ToArray();
        if ((!dropPod || payload.AcceptedMemoEventId is not null) && !memos.Any(memo => memo.EventId == payload.AcceptedMemoEventId)) return false;
        foreach (var memo in memos)
        {
            var memoPayload = (TradeEventPayload)memo.Payload;
            bool selected = memo.EventId == payload.AcceptedMemoEventId;
            if (memo.Status != (selected ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled)
                || selected && memo.Actor != exchange.Actor || memo.Actor.UserId == order.Actor.UserId
                || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                    new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId,
                        new(memo.Actor.UserId, memo.Actor.ColonyId, "committed"), memoPayload.PostagePaidByAcceptor))) return false;
        }
        string suffix = payload.AcceptedMemoEventId ?? exchange.Actor.UserId + ":" + exchange.Actor.ColonyId;
        var ownerDelivery = ledger.FindByIdempotencyKey($"trade-completed-owner-delivery:{order.EventId}:{suffix}");
        var buyerDelivery = ledger.FindByIdempotencyKey($"trade-completed-acceptor-delivery:{order.EventId}:{suffix}");
        if (!MatchesTradeCompletionDelivery(ownerDelivery, exchange.Actor, order.Actor, payload.RequestedItems,
                ItemDeliveryPurpose.TradeCompletedOwnerDelivery)
            || !SameTradeEvidence(ownerDelivery!.TargetContext, ResolveTradeCompletedOwnerDeliveryTargetContext(order, payload.FulfillmentMode))
            || (dropPod ? !MatchesTradeCompletionDelivery(buyerDelivery, order.Actor, exchange.Actor, payload.OfferedItems,
                    ItemDeliveryPurpose.TradeCompletedAcceptorDelivery) : buyerDelivery is not null)) return false;
        participants = memos.Select(memo => memo.Actor.UserId).Append(exchange.Actor.UserId).Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool MatchesTradeCompletionDelivery(AuthoritativeEvent? delivery, EventParty actor, EventParty target,
        IReadOnlyList<EventThingReference> items, ItemDeliveryPurpose purpose) => delivery is not null
        && delivery.Type == ServerEventType.ItemDelivery && delivery.Actor == actor && delivery.Target == target
        && delivery.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.Conflict or ServerEventStatus.RejectedByTarget)
        && delivery.TargetDecision != TargetEventDecision.Rejected
        && delivery.Payload is ItemDeliveryEventPayload { Message: null, DeliveryKind: null } payload
        && payload.Purpose == purpose && SameTradeEvidence(payload.Items, items);

    private static bool SameTradeEvidence<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
