using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

// A fixed decision, persisted in the same JSON document as the replacement baseline.
internal sealed record AdminBaselineTradeInvalidation(int Version, string OrderId, LedgerBatch Batch, IReadOnlyList<string> NotifyUsers)
{
    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(OrderId) || Batch?.Events is null || Batch.StatusChanges is null
            || NotifyUsers is null || NotifyUsers.Count == 0 || NotifyUsers.Any(string.IsNullOrWhiteSpace)
            || (Batch.ApplicationChanges?.Count ?? 0) != 0 || (Batch.ConfirmationChanges?.Count ?? 0) != 0
            || (Batch.RejectionChanges?.Count ?? 0) != 0
            || Batch.Events.Any(item => item?.Payload is null || item.Actor is null || item.Target is null)
            || Batch.StatusChanges.Any(change => change?.ExpectedEvent is null || change.EventId != change.ExpectedEvent.EventId
                || change.ExpectedEvent.Actor is null || change.ExpectedEvent.Target is null
                || change.ExpectedStatus != change.ExpectedEvent.Status || change.ExpectedPayload is not null || change.Payload is not null
                || change.Status != ServerEventStatus.Cancelled && change.Status != change.ExpectedStatus)
            || Batch.StatusChanges.Count(change => change.EventId == OrderId && change.Status == ServerEventStatus.Cancelled
                && change.ExpectedEvent is { Type: ServerEventType.Trade, Payload: TradeEventPayload { Stage: TradeStage.MarketOrder } }) != 1)
            throw new InvalidOperationException("Invalid pending administrator baseline trade invalidation.");

        var sources = Batch.StatusChanges.Select(change => change.ExpectedEvent!).ToArray();
        var order = sources.Single(item => item.EventId == OrderId);
        var trade = (TradeEventPayload)order.Payload;
        var memos = sources.Where(item => item.Type == ServerEventType.Trade && item.EventId != OrderId).ToArray();
        var targets = memos.Select(item => item.Target).Prepend(order.Actor)
            .GroupBy(party => (party.UserId, party.ColonyId)).Select(group => group.First()).ToArray();
        var deliveries = Batch.Events.Concat(sources.Where(item => item.Type != ServerEventType.Trade)).ToArray();
        if (Batch.StatusChanges.Any(change => change.ExpectedEvent!.Type == ServerEventType.Trade
                ? change.Status != ServerEventStatus.Cancelled : change.Status != change.ExpectedStatus)
            || memos.Any(item => item.Payload is not TradeEventPayload { Stage: TradeStage.AcceptedMemo } memo
                || memo.TradeId != OrderId || item.Actor.UserId != item.Target.UserId || item.Actor.ColonyId != item.Target.ColonyId
                || !string.IsNullOrWhiteSpace(item.Actor.FactionId) || !string.IsNullOrWhiteSpace(item.Target.FactionId)
                || item.Actor.UserId == order.Actor.UserId || !Same(item.TargetContext, order.TargetContext)
                || !Same(memo, new TradeEventPayload(OrderId, TradeStage.AcceptedMemo, trade.OfferedItems, trade.RequestedItems,
                    trade.FeeSilver, AcceptedByUserId: item.Actor.UserId, PostagePaidByAcceptor: memo.PostagePaidByAcceptor)))
            || !targets.Select(party => party.UserId).ToHashSet(StringComparer.Ordinal).SetEquals(NotifyUsers)
            || deliveries.Any(item => item.Type is not (ServerEventType.ItemDelivery or ServerEventType.ServerNotification)
                || !AuthoritativeEventFactory.IsKnownEventId(item.Type, item.IdempotencyKey, item.EventId)
                || item.Actor != new EventParty("server") || item.TargetDecision == TargetEventDecision.Rejected
                || item.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.Conflict or ServerEventStatus.RejectedByTarget)
            || deliveries.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != deliveries.Length
            || deliveries.Count(item => item.Type == ServerEventType.ItemDelivery) != 1
            || deliveries.Count(item => item.Type == ServerEventType.ServerNotification) != targets.Length)
            throw new InvalidOperationException("Incomplete pending baseline cancellation graph.");
        var refund = deliveries.Single(item => item.Type == ServerEventType.ItemDelivery);
        if (refund.Payload is not ItemDeliveryEventPayload { Message: null, DeliveryKind: null } payload
            || refund.Target != order.Actor || !Same(refund.TargetContext, order.TargetContext) || !Same(payload.Items, trade.OfferedItems)
            || refund.IdempotencyKey != (payload.Purpose switch
            {
                ItemDeliveryPurpose.TradeCancelledOwnerReturn => "trade-cancelled-owner-return:",
                ItemDeliveryPurpose.TradeExpiredOwnerReturn => "trade-expired-owner-return:",
                ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn => "trade-baseline-invalidated-owner-return:",
                _ => throw new InvalidOperationException("Invalid pending baseline refund purpose.")
            }) + OrderId)
            throw new InvalidOperationException("Invalid pending baseline refund.");
        foreach (var target in targets)
        {
            string key = $"trade-baseline-invalidated:{OrderId}:{target.UserId}:{target.ColonyId}";
            if (deliveries.Count(item => item.Type == ServerEventType.ServerNotification && item.Target == target
                && item.IdempotencyKey == key && item.TargetContext is null
                && item.Payload is ServerNotificationEventPayload { Severity: ServerNotificationSeverity.Warning, FromAdministrator: false } notification
                && notification.NotificationId == key) != 1)
                throw new InvalidOperationException("Invalid pending baseline participant notification.");
        }
    }

    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
