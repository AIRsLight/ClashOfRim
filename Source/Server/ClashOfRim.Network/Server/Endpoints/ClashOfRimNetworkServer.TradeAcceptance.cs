using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult AcceptTradeOrder(AcceptTradeOrderRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolResponse rejection) => Results.Ok(new AcceptTradeOrderResponse(
            rejection, request.TradeEventId, null, false));
        if (request.Acceptor is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Auth.IdentityDenied")));
        using (var mutation = EnterColonySnapshotMutation(state, request.AuthToken, request.Acceptor.UserId,
            request.Acceptor.ColonyId ?? string.Empty))
        {
            if (mutation.Rejection is { } rejection) return Reject(rejection);
            ApplyRecoveredTradeOrderExpirations(state, DateTimeOffset.UtcNow);

            ProtocolResponse? validation = ValidateTradeAcceptanceRequest(request);
            if (validation is not null)
            {
                return Results.Ok(new AcceptTradeOrderResponse(
                    validation,
                    request.TradeEventId,
                    memoEventId: null,
                    memoCreated: false));
            }

            AuthoritativeEvent? tradeOrder = state.Ledger.Find(request.TradeEventId);
            if (tradeOrder is null || !IsOpenMarketTradeOrder(tradeOrder, request.Acceptor.UserId))
            {
                return Results.Ok(new AcceptTradeOrderResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Trade.OrderNotAcceptable")),
                    request.TradeEventId,
                    memoEventId: null,
                    memoCreated: false));
            }

            IResult Conflict() => Results.Ok(new AcceptTradeOrderResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade acceptance conflicts with an existing memo or idempotency key."),
                request.TradeEventId, null, false));
            var byKey = state.Ledger.FindByIdempotencyKey(request.IdempotencyKey);
            if (byKey is not null && !MatchesTradeAcceptance(byKey, tradeOrder, request)) return Conflict();

            // A second key may retry the same active acceptance, but cannot mask another key's owner.
            var candidates = FindActiveAcceptanceCandidates(state.Ledger, tradeOrder.EventId, request.Acceptor);
            if (candidates.Length > 1 || candidates.Any(item => !MatchesTradeAcceptance(item, tradeOrder, request))) return Conflict();
            AuthoritativeEvent? existingMemo = candidates.SingleOrDefault();
            if (existingMemo is not null)
            {
                return Results.Ok(new AcceptTradeOrderResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
                    request.TradeEventId,
                    existingMemo.EventId,
                    memoCreated: false));
            }

            if (request.PostagePaidByAcceptor)
            {
                TradePostageQuoteDto postage = BuildTradePostageQuote(
                    state,
                    tradeOrder,
                    request.Acceptor.UserId,
                    request.Acceptor.ColonyId ?? string.Empty,
                    request.Acceptor.SnapshotId ?? string.Empty);
                if (!postage.Reachable || postage.PostageSilver is null)
                {
                    return Results.Ok(new AcceptTradeOrderResponse(
                        ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            T("Trade.DropPodUnavailable", ("STATUS", postage.Status))),
                        request.TradeEventId,
                        memoEventId: null,
                        memoCreated: false));
                }
            }

            var acceptorParty = ToEventParty(request.Acceptor);
            TradeEventPayload orderPayload = (TradeEventPayload)tradeOrder.Payload;
            AuthoritativeEvent memoEvent = AuthoritativeEventFactory.Create(
                ServerEventType.Trade,
                acceptorParty,
                acceptorParty,
                request.IdempotencyKey,
                targetOnline: state.OnlinePresence.IsUserOnline(request.Acceptor.UserId),
                new TradeEventPayload(
                    tradeOrder.EventId,
                    TradeStage.AcceptedMemo,
                    orderPayload.OfferedItems,
                    orderPayload.RequestedItems,
                    orderPayload.FeeSilver,
                    AcceptedByUserId: request.Acceptor.UserId,
                    FulfillmentMode: TradeFulfillmentMode.Unspecified,
                    PostagePaidByAcceptor: request.PostagePaidByAcceptor),
                DateTimeOffset.UtcNow,
                tradeOrder.TargetContext) with
            {
                Status = ServerEventStatus.Recorded,
                LastApplicationResult = EventApplicationResultKind.ReadyToApply
            };

            LedgerAppendResult append;
            try
            {
                memoEvent = AuthoritativeEventFactory.ResolvePersistedIdentity(state.Ledger, memoEvent);
                append = state.Ledger.Append(memoEvent);
            }
            catch (InvalidOperationException) { return Conflict(); }
            // Append protects identity; business equivalence remains this endpoint's responsibility.
            if (!MatchesTradeAcceptance(append.Event, tradeOrder, request)) return Conflict();
            LogEventAppend(state, append, "trade-order-accepted-memo");

            return Results.Ok(new AcceptTradeOrderResponse(
                append.Created
                    ? ProtocolResponse.Ok(ServerLocalization.Text("Trade.OrderAccepted"))
                    : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Trade.OrderAcceptedDuplicate")),
                tradeOrder.EventId,
                append.Event.EventId,
                append.Created));
        }
    }

    private static AuthoritativeEvent[] FindActiveAcceptanceCandidates(IAuthoritativeEventLedger ledger, string orderId, ProtocolIdentity identity) =>
        ledger.ListForUser(identity.UserId).Where(item =>
            item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo } payload && payload.TradeId == orderId
            && item.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget
                or ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Conflict)
            && (IsVisibleParty(item.Actor, identity.UserId, identity.ColonyId!)
                || IsVisibleParty(item.Target, identity.UserId, identity.ColonyId!))).ToArray();

    private static bool MatchesTradeAcceptance(AuthoritativeEvent memo, AuthoritativeEvent order, AcceptTradeOrderRequest request)
    {
        bool SameParty(EventParty party) => party.UserId == request.Acceptor.UserId
            && party.ColonyId == request.Acceptor.ColonyId && string.IsNullOrWhiteSpace(party.FactionId);
        if (!IsActiveTradeAcceptanceMemo(memo) || !SameParty(memo.Actor) || !SameParty(memo.Target)
            || memo.Payload is not TradeEventPayload payload || order.Payload is not TradeEventPayload original)
            return false;
        var expected = new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, original.OfferedItems, original.RequestedItems,
            original.FeeSilver, AcceptedByUserId: request.Acceptor.UserId, PostagePaidByAcceptor: request.PostagePaidByAcceptor);
        return JsonSerializer.Serialize(payload) == JsonSerializer.Serialize(expected)
            && JsonSerializer.Serialize(memo.TargetContext) == JsonSerializer.Serialize(order.TargetContext);
    }
}
