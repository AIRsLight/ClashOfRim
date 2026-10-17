using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal static class TradeFailureEvidence
{
    internal sealed record TradeFailureGraph(AuthoritativeEvent Order, AuthoritativeEvent Exchange,
        AuthoritativeEvent[] Memos, AuthoritativeEvent[] Deliveries, bool AppliedDelivery,
        AuthoritativeEvent? OwnerReturn, AuthoritativeEvent? BuyerReturn)
    {
        public IEnumerable<AuthoritativeEvent> Events => Memos.Concat(Deliveries).Prepend(Exchange).Prepend(Order);
        public bool RefundBuyer => !AppliedDelivery && Exchange.Status == ServerEventStatus.AppliedToSnapshot;
    }

    internal static bool TryReadTradeFailureGraph(IAuthoritativeEventLedger ledger, AuthoritativeEvent root,
        string orderId, out TradeFailureGraph? graph)
    {
        graph = null;
        var order = ledger.Find(orderId);
        if (order?.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder } original) return false;
        var all = ledger.ListAll();
        var related = all.Where(item => item.Payload is TradeEventPayload p && p.TradeId == orderId).ToArray();
        var exchanges = related.Where(item => item.Payload is TradeEventPayload
            { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange }).ToArray();
        if (exchanges.Length != 1) return false;
        var exchange = exchanges[0]; var payload = (TradeEventPayload)exchange.Payload;
        bool self = payload.Stage == TradeStage.SelfDeliveryExchange;
        var mode = self ? TradeFulfillmentMode.SelfDelivery : TradeFulfillmentMode.ServerDropPod;
        if (exchange.Type != ServerEventType.Trade || exchange.Target != order.Actor
            || string.IsNullOrWhiteSpace(exchange.Actor.UserId) || string.IsNullOrWhiteSpace(exchange.Actor.ColonyId)
            || exchange.Actor.UserId == order.Actor.UserId || payload.AcceptedByUserId is not null && payload.AcceptedByUserId != exchange.Actor.UserId
            || payload.FulfillmentMode != TradeFulfillmentMode.Unspecified && payload.FulfillmentMode != mode
            || original.FulfillmentMode != TradeFulfillmentMode.Unspecified && original.FulfillmentMode != mode
            || payload.FeeSilver != original.FeeSilver || !SameTradeEvidence(payload.OfferedItems, original.OfferedItems)
            || !SameTradeEvidence(exchange.TargetContext, order.TargetContext)
            || exchange.Status is ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget
            || exchange.Status != ServerEventStatus.AppliedToSnapshot && (!string.IsNullOrWhiteSpace(exchange.AppliedSnapshotId) || exchange.AppliedAtUtc is not null)
            || exchange.Status == ServerEventStatus.AppliedToSnapshot && (string.IsNullOrWhiteSpace(exchange.AppliedSnapshotId)
                || exchange.AppliedAtUtc is null || exchange.LastApplicationResult != EventApplicationResultKind.Applied)) return false;

        var memos = related.Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo }).ToArray();
        foreach (var memo in memos)
        {
            var p = (TradeEventPayload)memo.Payload;
            if (string.IsNullOrWhiteSpace(memo.Actor.UserId) || string.IsNullOrWhiteSpace(memo.Actor.ColonyId)
                || memo.Actor.UserId == order.Actor.UserId
                || memo.Status is ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget or ServerEventStatus.Conflict
                || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                    new(memo.IdempotencyKey, orderId, new(memo.Actor.UserId, memo.Actor.ColonyId, "failure"), p.PostagePaidByAcceptor))) return false;
        }

        if (payload.AcceptedMemoEventId is { } memoId && !memos.Any(memo => memo.EventId == memoId && memo.Actor == exchange.Actor)) return false;
        string suffix = payload.AcceptedMemoEventId ?? exchange.Actor.UserId + ":" + exchange.Actor.ColonyId;
        string ownerKey = $"trade-completed-owner-delivery:{orderId}:{suffix}";
        string buyerKey = $"trade-completed-acceptor-delivery:{orderId}:{suffix}";
        var ownerDelivery = ledger.FindByIdempotencyKey(ownerKey); var buyerDelivery = ledger.FindByIdempotencyKey(buyerKey);
        var deliveries = all.Where(item => item.Payload is ItemDeliveryEventPayload { IsTradeDelivery: true }
            && IsTradeCompletedDeliveryIdempotencyKey(item.IdempotencyKey, orderId)).ToArray();
        // A pre-batch legacy exchange could have been saved before either delivery was created.
        bool unconfirmedLegacyExchange = root.EventId == exchange.EventId && (IsOpenMarketTradeOrderForOwner(order)
                || order.Status == ServerEventStatus.Cancelled && root.Status == ServerEventStatus.Failed)
            && exchange.Status != ServerEventStatus.AppliedToSnapshot && ownerDelivery is null && buyerDelivery is null && deliveries.Length == 0;
        if (!unconfirmedLegacyExchange && (!MatchesFailureTradeDelivery(ownerDelivery, exchange.Actor, order.Actor, payload.RequestedItems,
                ItemDeliveryPurpose.TradeCompletedOwnerDelivery)
            || !SameTradeEvidence(ownerDelivery!.TargetContext, ResolveTradeCompletedOwnerDeliveryTargetContext(order, mode))
            || (self ? buyerDelivery is not null : !MatchesFailureTradeDelivery(buyerDelivery, order.Actor, exchange.Actor,
                payload.OfferedItems, ItemDeliveryPurpose.TradeCompletedAcceptorDelivery))
            || deliveries.Length != (self ? 1 : 2))) return false;
        if (root.EventId != exchange.EventId && !deliveries.Any(item => item.EventId == root.EventId)) return false;

        bool applied = self && exchange.Status == ServerEventStatus.AppliedToSnapshot
            || deliveries.Any(item => item.Status == ServerEventStatus.AppliedToSnapshot);
        string ownerReturnKey = "trade-application-failed-owner-return:" + orderId;
        string buyerReturnKey = $"trade-application-failed-acceptor-return:{orderId}:{exchange.EventId}";
        var ownerReturns = all.Where(item => item.IdempotencyKey == ownerReturnKey
            || item.IdempotencyKey.StartsWith(ownerReturnKey + ":", StringComparison.Ordinal)).ToArray();
        var buyerReturns = all.Where(item => item.IdempotencyKey.StartsWith("trade-application-failed-acceptor-return:" + orderId + ":", StringComparison.Ordinal)).ToArray();
        if (ownerReturns.Length > 1 || buyerReturns.Length > 1
            || Enum.GetValues<TradeCancellationReason>().Any(reason => ledger.FindByIdempotencyKey(TradeCancellationReturn(reason).Prefix + orderId) is not null)
            || applied && (ownerReturns.Length != 0 || buyerReturns.Length != 0)) return false;
        var ownerReturn = ownerReturns.SingleOrDefault(); var buyerReturn = buyerReturns.SingleOrDefault();
        if (ownerReturn is not null && (ownerReturn.IdempotencyKey != ownerReturnKey
                && !deliveries.Append(exchange).Any(item => ownerReturn.IdempotencyKey == ownerReturnKey + ":" + item.EventId)
            || !MatchesFailureTradeReturn(ownerReturn, order.Actor, original.OfferedItems, ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn)
            || !SameTradeEvidence(ownerReturn.TargetContext, order.TargetContext))) return false;
        if (buyerReturn is not null && (exchange.Status != ServerEventStatus.AppliedToSnapshot || self || buyerReturn.IdempotencyKey != buyerReturnKey
            || !MatchesFailureTradeReturn(buyerReturn, exchange.Actor, payload.RequestedItems, ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn))) return false;

        graph = new(order, exchange, memos, deliveries, applied, ownerReturn, buyerReturn);
        // A terminal root only replays a complete, previously persisted failure decision.
        if (root.Status == ServerEventStatus.Failed || root.Status == ServerEventStatus.Conflict && EventFailureNotifications.FindFailureNotifications(ledger, root).Count > 0)
        {
            var terminal = applied ? ServerEventStatus.Conflict : ServerEventStatus.Failed;
            if (order.Status != (applied ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled)
                || memos.Any(memo => memo.Status != ServerEventStatus.Cancelled)
                || deliveries.Append(exchange).Any(item => item.Status != ServerEventStatus.AppliedToSnapshot && item.Status != terminal)
                || !applied && (ownerReturn is null || graph.RefundBuyer && buyerReturn is null)) return false;
        }

        return true;
    }

    internal static bool MatchesFailureTradeDelivery(AuthoritativeEvent? item, EventParty actor, EventParty target,
        IReadOnlyList<EventThingReference> items, ItemDeliveryPurpose purpose) => item?.Type == ServerEventType.ItemDelivery
        && item.Actor == actor && item.Target == target && item.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
        && item.TargetDecision != TargetEventDecision.Rejected && item.Payload is ItemDeliveryEventPayload { Message: null, DeliveryKind: null } payload
        && payload.Purpose == purpose && SameTradeEvidence(payload.Items, items);

    internal static bool MatchesFailureTradeReturn(AuthoritativeEvent item, EventParty target, IReadOnlyList<EventThingReference> items,
        ItemDeliveryPurpose purpose) => MatchesTradeCompletionDelivery(item, new("server"), target, items, purpose)
        && AuthoritativeEventFactory.IsKnownEventId(item.Type, item.IdempotencyKey, item.EventId);

    internal static bool TryResolveTradeOrderIdFromDelivery(
        IAuthoritativeEventLedger ledger,
        AuthoritativeEvent ledgerEvent,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        if (ledgerEvent.Type != ServerEventType.ItemDelivery
            || ledgerEvent.Payload is not ItemDeliveryEventPayload { IsTradeDelivery: true }
            || IsTradeApplicationFailedOwnerReturnEvent(ledgerEvent))
        {
            return false;
        }

        if (!TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(ledgerEvent.IdempotencyKey, out string? parsedTradeOrderId))
        {
            return false;
        }

        AuthoritativeEvent? tradeOrder = ledger.Find(parsedTradeOrderId!);
        if (tradeOrder is null
            || tradeOrder.Type != ServerEventType.Trade
            || tradeOrder.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder })
        {
            return false;
        }

        tradeOrderId = tradeOrder.EventId;
        return true;
    }

    internal static bool IsTradeApplicationFailedOwnerReturnEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.ItemDelivery
            && (ledgerEvent.IdempotencyKey.StartsWith("trade-application-failed-owner-return:", StringComparison.Ordinal)
                || ledgerEvent.IdempotencyKey.StartsWith("trade-application-failed-acceptor-return:", StringComparison.Ordinal)
                || ledgerEvent.Payload is ItemDeliveryEventPayload
                {
                    Purpose: ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn
                    or ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn
                });
    }

    internal static bool IsTradeCompletedDeliveryIdempotencyKey(string idempotencyKey, string tradeOrderId)
    {
        return IsTradeCompletedDeliveryIdempotencyKey(idempotencyKey, tradeOrderId, "trade-completed-owner-delivery:")
            || IsTradeCompletedDeliveryIdempotencyKey(idempotencyKey, tradeOrderId, "trade-completed-acceptor-delivery:");
    }

    internal static bool TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        return TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(idempotencyKey, "trade-completed-owner-delivery:", out tradeOrderId)
            || TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(idempotencyKey, "trade-completed-acceptor-delivery:", out tradeOrderId);
    }

    internal static bool TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        string prefix,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || !idempotencyKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = idempotencyKey[prefix.Length..];
        int typeSeparator = rest.IndexOf(':');
        if (typeSeparator <= 0)
        {
            return false;
        }

        int idEnd = rest.IndexOf(':', typeSeparator + 1);
        if (idEnd <= typeSeparator + 1)
        {
            return false;
        }

        tradeOrderId = rest[..idEnd];
        return true;
    }

    internal static bool IsTradeCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        string tradeOrderId,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || string.IsNullOrWhiteSpace(tradeOrderId)
            || !idempotencyKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = idempotencyKey[prefix.Length..];
        return rest.StartsWith(tradeOrderId + ":", StringComparison.Ordinal);
    }

}
