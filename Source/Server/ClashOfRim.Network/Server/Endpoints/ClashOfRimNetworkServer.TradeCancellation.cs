using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private enum TradeCancellationReason { Owner, Expired, BaselineChanged }

    // Callers own their mutation scope and any required authentication.
    private static bool TryPrepareOwnedTradeCancellation(ClashOfRimNetworkState state, AuthoritativeEvent order,
        out AuthoritativeEvent[] memos, out LedgerBatch? batch)
        => TryPrepareTradeCancellation(state, order, TradeCancellationReason.Owner, DateTimeOffset.UtcNow, out memos, out batch);

    private static (string Prefix, ItemDeliveryPurpose Purpose) TradeCancellationReturn(TradeCancellationReason reason) => reason switch
    {
        TradeCancellationReason.Expired => ("trade-expired-owner-return:", ItemDeliveryPurpose.TradeExpiredOwnerReturn),
        TradeCancellationReason.BaselineChanged => ("trade-baseline-invalidated-owner-return:", ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn),
        _ => ("trade-cancelled-owner-return:", ItemDeliveryPurpose.TradeCancelledOwnerReturn)
    };

    // A closed order alone is not proof of a refund; all three cancellation causes share this decision.
    private static bool TryPrepareTradeCancellation(ClashOfRimNetworkState state, AuthoritativeEvent order,
        TradeCancellationReason reason, DateTimeOffset nowUtc, out AuthoritativeEvent[] memos, out LedgerBatch? batch)
    {
        batch = null;
        memos = [];
        if (order.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
            || order.Status != ServerEventStatus.Cancelled && !IsOpenMarketTradeOrderForOwner(order)) return false;
        var all = state.Ledger.ListAll();
        var related = all.Where(item => item.Payload is TradeEventPayload p && p.TradeId == order.EventId).ToArray();
        memos = related.Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo }).ToArray();
        if (related.Any(item => item.Payload is TradeEventPayload
                { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange })
            || all.Any(item => item.IdempotencyKey.StartsWith($"trade-completed-owner-delivery:{order.EventId}:", StringComparison.Ordinal)
                || item.IdempotencyKey.StartsWith($"trade-completed-acceptor-delivery:{order.EventId}:", StringComparison.Ordinal)
                || item.IdempotencyKey == $"trade-application-failed-owner-return:{order.EventId}"
                || item.IdempotencyKey.StartsWith($"trade-application-failed-acceptor-return:{order.EventId}:", StringComparison.Ordinal))) return false;
        foreach (var memo in memos)
        {
            var payload = (TradeEventPayload)memo.Payload;
            if (memo.Status != ServerEventStatus.Cancelled && !IsActiveTradeAcceptanceMemo(memo)
                || string.IsNullOrWhiteSpace(memo.Actor.UserId) || string.IsNullOrWhiteSpace(memo.Actor.ColonyId)
                || memo.Actor.UserId == order.Actor.UserId
                || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                    new(memo.IdempotencyKey, order.EventId, new(memo.Actor.UserId, memo.Actor.ColonyId, "cancel"), payload.PostagePaidByAcceptor))) return false;
        }
        if (memos.Where(IsActiveTradeAcceptanceMemo).GroupBy(memo => (memo.Actor.UserId, memo.Actor.ColonyId)).Any(group => group.Count() > 1)) return false;

        // Expiry and baseline changes also close orders. Reuse their refund, never issue a second one.
        AuthoritativeEvent? returned = null;
        foreach (var (prefix, purpose) in Enum.GetValues<TradeCancellationReason>().Select(TradeCancellationReturn))
        {
            var candidate = state.Ledger.FindByIdempotencyKey(prefix + order.EventId);
            if (candidate is null) continue;
            if (returned is not null || !MatchesTradeCompletionDelivery(candidate, new("server"), order.Actor,
                    ((TradeEventPayload)order.Payload).OfferedItems, purpose)
                || !AuthoritativeEventFactory.IsKnownEventId(candidate.Type, candidate.IdempotencyKey, candidate.EventId)
                || !SameTradeEvidence(candidate.TargetContext, order.TargetContext)) return false;
            returned = candidate;
        }
        if (order.Status == ServerEventStatus.Cancelled && returned is null) return false;
        if (order.Status == ServerEventStatus.Cancelled && memos.All(memo => memo.Status == ServerEventStatus.Cancelled)) return true;

        var changes = memos.Select(memo => new LedgerStatusChange(memo.EventId, memo.Status,
                ServerEventStatus.Cancelled, ExpectedEvent: memo))
            .Prepend(new(order.EventId, order.Status, ServerEventStatus.Cancelled, ExpectedEvent: order)).ToList();
        AuthoritativeEvent[] events = [];
        if (returned is not null)
            changes.Add(new(returned.EventId, returned.Status, returned.Status, ExpectedEvent: returned));
        else
        {
            var (prefix, purpose) = TradeCancellationReturn(reason);
            events = [CreateTradeItemDeliveryEvent(state, prefix + order.EventId,
                new("server"), order.Actor, ((TradeEventPayload)order.Payload).OfferedItems,
                purpose, order.TargetContext, nowUtc)];
        }
        batch = new(events, changes);
        return true;
    }
}
