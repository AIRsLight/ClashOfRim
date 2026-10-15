using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // Called under the authenticated CancelTradeOrder mutation scope.
    private static IResult CancelTradeAcceptanceMemo(CloseTradeOrderRequest request, ClashOfRimNetworkState state, AuthoritativeEvent order)
    {
        var byKey = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
        if (byKey is not null) return ReplayTradeMemoCancellation(request, state, order, byKey, duplicate: true);
        if (!IsOpenMarketTradeOrderForOwner(order)) return MemoCancellationConflict(request);
        var candidates = FindActiveAcceptanceCandidates(state.Ledger, order.EventId, request.Owner);
        if (candidates.Length == 0)
            return Results.Ok(new CloseTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound,
                T("Trade.AcceptanceMemoNotFound")), order.EventId, string.Empty, 0));
        if (candidates.Length != 1) return MemoCancellationConflict(request);
        var memo = candidates[0]; var payload = (TradeEventPayload)memo.Payload;
        var acceptance = new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId, request.Owner, payload.PostagePaidByAcceptor);
        if (!MatchesTradeAcceptance(memo, order, acceptance)) return MemoCancellationConflict(request);

        var receipt = AuthoritativeEventFactory.Create(ServerEventType.Trade, memo.Actor, order.Actor, request.IdempotencyKey, false,
            payload with { Stage = TradeStage.AcceptanceCancelled, AcceptedMemoEventId = memo.EventId }, DateTimeOffset.UtcNow,
            order.TargetContext) with { Status = ServerEventStatus.Cancelled };
        try
        {
            state.Ledger.ApplyBatch(new([receipt], [new(memo.EventId, memo.Status, ServerEventStatus.Cancelled, ExpectedEvent: memo)]));
        }
        catch (InvalidOperationException) { return MemoCancellationConflict(request); }
        return ReplayTradeMemoCancellation(request, state, order, receipt, duplicate: false);
    }

    private static IResult ReplayTradeMemoCancellation(CloseTradeOrderRequest request, ClashOfRimNetworkState state,
        AuthoritativeEvent? order, AuthoritativeEvent receipt, bool duplicate)
    {
        if (order?.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
            || receipt.Type != ServerEventType.Trade || receipt.Status != ServerEventStatus.Cancelled
            || receipt.IdempotencyKey != request.IdempotencyKey
            || !AuthoritativeEventFactory.IsKnownEventId(receipt.Type, receipt.IdempotencyKey, receipt.EventId)
            || receipt.Payload is not TradeEventPayload { Stage: TradeStage.AcceptanceCancelled } payload
            || payload.TradeId != order.EventId || receipt.Target != order.Actor
            || receipt.Actor.UserId != request.Owner.UserId || receipt.Actor.ColonyId != request.Owner.ColonyId
            || payload.AcceptedMemoEventId is null)
            return MemoCancellationConflict(request);
        var memo = state.Ledger.Find(payload.AcceptedMemoEventId);
        if (memo?.Status != ServerEventStatus.Cancelled || memo.Payload is not TradeEventPayload original
            || memo.Actor != receipt.Actor
            || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId, request.Owner, original.PostagePaidByAcceptor))
            || JsonSerializer.Serialize(payload) != JsonSerializer.Serialize(original with
                { Stage = TradeStage.AcceptanceCancelled, AcceptedMemoEventId = memo.EventId })
            || JsonSerializer.Serialize(receipt.TargetContext) != JsonSerializer.Serialize(order.TargetContext))
            return MemoCancellationConflict(request);

        // The durable receipt chooses the original memo even after another acceptance or an order closure.
        string[] users = [order.Actor.UserId, request.Owner.UserId];
        state.EventNotifications.SignalUsers(users);
        return Results.Ok(new CloseTradeOrderResponse(new ProtocolResponse(true,
            duplicate ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, T("Trade.AcceptanceMemoCancelled")),
            order.EventId, ServerEventStatus.Cancelled.ToString(), users.Distinct(StringComparer.Ordinal).Count()));
    }

    private static IResult MemoCancellationConflict(CloseTradeOrderRequest request) => Results.Ok(new CloseTradeOrderResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade memo cancellation conflicts with its original acceptance."),
        request.TradeEventId, string.Empty, 0));
}
