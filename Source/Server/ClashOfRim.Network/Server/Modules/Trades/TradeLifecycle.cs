using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeLifecycle(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotMutationCoordinator mutations, object gate, TradeDelivery delivery, AdminBaselineRegistry baseline,
    OnlinePresenceRegistry presence, EventNotificationHub notifications, ILogger logger,
    Func<ClashOfRimServerConfiguration> configuration)
{
    internal bool TryPrepareOwnedTradeCancellation(AuthoritativeEvent order,
        out AuthoritativeEvent[] memos, out LedgerBatch? batch)
        => TryPrepareTradeCancellation(order, TradeCancellationReason.Owner, DateTimeOffset.UtcNow, out memos, out batch);

    // A closed order alone is not proof of a refund; all cancellation causes share this decision.
    private bool TryPrepareTradeCancellation(AuthoritativeEvent order,
        TradeCancellationReason reason, DateTimeOffset nowUtc, out AuthoritativeEvent[] memos, out LedgerBatch? batch)
    {
        batch = null;
        memos = [];
        if (order.Type != ServerEventType.Trade || order.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
            || order.Status != ServerEventStatus.Cancelled && !IsOpenMarketTradeOrderForOwner(order)) return false;
        var all = ledger.ListAll();
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
            var candidate = ledger.FindByIdempotencyKey(prefix + order.EventId);
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
            events = [delivery.CreateTradeItemDeliveryEvent(prefix + order.EventId,
                new("server"), order.Actor, ((TradeEventPayload)order.Payload).OfferedItems,
                purpose, order.TargetContext, nowUtc)];
        }

        batch = new(events, changes);
        return true;
    }

    internal void ApplyTradeOrderExpirations(DateTimeOffset nowUtc)
    {
        using (mutations.Enter()) ApplyRecoveredTradeOrderExpirations(nowUtc);
    }

    // Authorized callers already own the recovered mutation scope. Do not recover again here.
    internal void ApplyRecoveredTradeOrderExpirations(DateTimeOffset nowUtc)
    {
        if (!Monitor.IsEntered(gate))
            throw new InvalidOperationException("Trade expiration requires the recovered mutation scope.");
        TimeSpan expiration = configuration().TradeOrderExpiration;
        if (expiration <= TimeSpan.Zero) return;
        var expiredOrders = ledger.ListByType(ServerEventType.Trade)
            .Where(IsOpenMarketTradeOrderForOwner)
            .Where(order => nowUtc - order.CreatedAtUtc >= expiration).ToArray();
        foreach (var order in expiredOrders)
            ApplyAutomaticTradeCancellation(order, TradeCancellationReason.Expired, [], nowUtc);
    }

    internal int ApplyTradeOrderBaselineInvalidations(AdminBaselineSnapshot baseline, DateTimeOffset nowUtc)
    {
        using (mutations.Enter())
        {
            int cancelled = 0;
            foreach (var work in PrepareTradeOrderBaselineInvalidations(baseline, nowUtc))
            {
                if (ApplyPreparedCancellation(work.OrderId, TradeCancellationReason.BaselineChanged, work.Batch, work.NotifyUsers))
                    cancelled++;
            }

            return cancelled;
        }

    }

    private bool ApplyAutomaticTradeCancellation(AuthoritativeEvent order,
        TradeCancellationReason reason, IReadOnlyList<string> unavailableDefs, DateTimeOffset nowUtc)
    {
        if (!TryPrepareAutomaticTradeCancellation(order, reason, unavailableDefs, nowUtc, out var batch, out var users)) return false;
        return ApplyPreparedCancellation(order.EventId, reason, batch!, users);
    }

    private bool ApplyPreparedCancellation(string orderId, TradeCancellationReason reason,
        LedgerBatch batch, IReadOnlyList<string> users)
    {
        try { ledger.ApplyBatch(batch); }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Automatic trade cancellation skipped conflicting evidence: order={OrderId} reason={Reason}", orderId, reason);
            return false;
        }

        notifications.SignalUsers(users);
        return true;
    }

    private bool TryPrepareAutomaticTradeCancellation(AuthoritativeEvent order,
        TradeCancellationReason reason, IReadOnlyList<string> unavailableDefs, DateTimeOffset nowUtc,
        out LedgerBatch? prepared, out string[] notifyUsers)
    {
        prepared = null;
        notifyUsers = [];
        bool Conflict()
        {
            logger.LogWarning("Automatic trade cancellation skipped conflicting evidence: order={OrderId} reason={Reason}", order.EventId, reason);
            return false;
        }

        if (!TryPrepareTradeCancellation(order, reason, nowUtc, out var memos, out var batch) || batch is null) return Conflict();
        var targets = memos.Select(memo => memo.Target).Prepend(order.Actor)
            .GroupBy(party => (party.UserId, party.ColonyId)).Select(group => group.First()).ToArray();
        var events = batch.Events.ToList();
        var changes = batch.StatusChanges.ToList();
        try
        {
            foreach (var target in targets)
            {
                bool owner = target.UserId == order.Actor.UserId && target.ColonyId == order.Actor.ColonyId;
                var notification = reason == TradeCancellationReason.Expired
                    ? CreateTradeExpiredNotification(order, target, owner, nowUtc)
                    : CreateTradeBaselineInvalidatedNotification(order, target, owner, unavailableDefs, nowUtc);
                var existing = ledger.FindByIdempotencyKey(notification.IdempotencyKey);
                if (existing is null) events.Add(notification);
                else
                {
                    if (!MatchesAutomaticTradeNotification(existing, notification)) return Conflict();
                    // Preserve an already consumed notification, including its original localized text.
                    changes.Add(new(existing.EventId, existing.Status, existing.Status, ExpectedEvent: existing));
                }

            }

            prepared = new(events, changes);
            LedgerBatchValidation.Validate(prepared, ledger.Find, ledger.FindByIdempotencyKey);
        }

        catch (InvalidOperationException) { return Conflict(); }
        notifyUsers = targets.Select(target => target.UserId).Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    internal IReadOnlyList<AdminBaselineTradeInvalidation> PrepareTradeOrderBaselineInvalidations(
        AdminBaselineSnapshot baseline, DateTimeOffset nowUtc)
    {
        var validDefs = BuildValidTradeThingDefSet(baseline, configuration());
        var work = new List<AdminBaselineTradeInvalidation>();
        if (validDefs.Count == 0) return work;
        foreach (var order in ledger.ListByType(ServerEventType.Trade).Where(IsOpenMarketTradeOrderForOwner).ToArray())
        {
            var payload = (TradeEventPayload)order.Payload;
            var unavailable = FindUnavailableTradeThingDefs(payload.OfferedItems.Concat(payload.RequestedItems), baseline, validDefs);
            if (unavailable.Count > 0 && TryPrepareAutomaticTradeCancellation(order, TradeCancellationReason.BaselineChanged,
                    unavailable, nowUtc, out var batch, out var users))
                work.Add(new(1, order.EventId, batch!, users));
        }

        return work;
    }

    internal void RecoverAdminBaselineTradeInvalidations()
    {
        using (mutations.EnterUnrecovered())
        {
            foreach (var work in baseline.ListPendingTradeInvalidations())
            {
                work.Validate();
                // With no new events, ApplyBatch has no insertion evidence for its normal replay check.
                // Such a batch is complete only if every frozen source is exactly its expected final state.
                bool completedWithoutInsertions = work.Batch.Events.Count == 0 && work.Batch.StatusChanges.All(change =>
                    SameTradeEvidence(ledger.Find(change.EventId), change.ExpectedEvent! with { Status = change.Status }));
                if (!completedWithoutInsertions) ledger.ApplyBatch(work.Batch);
                notifications.SignalUsers(work.NotifyUsers);
                baseline.CompleteTradeInvalidation(work.OrderId);
            }

        }

    }

    private bool MatchesAutomaticTradeNotification(AuthoritativeEvent existing, AuthoritativeEvent expected) =>
        existing.Type == ServerEventType.ServerNotification && existing.Actor == expected.Actor && existing.Target == expected.Target
        && AuthoritativeEventFactory.IsKnownEventId(existing.Type, existing.IdempotencyKey, existing.EventId)
        && existing.Status is not (ServerEventStatus.Failed or ServerEventStatus.Conflict or ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
        && existing.TargetDecision != TargetEventDecision.Rejected && SameTradeEvidence(existing.TargetContext, expected.TargetContext)
        && existing.Payload is ServerNotificationEventPayload payload && expected.Payload is ServerNotificationEventPayload expectedPayload
        && SameTradeEvidence(payload with { Title = expectedPayload.Title, Message = expectedPayload.Message }, expectedPayload);

    private AuthoritativeEvent CreateTradeExpiredNotification(
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-expired:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string message = targetIsOwner
            ? Format("Trade.ExpiredOwnerMessage", ("EVENT", tradeOrder.EventId))
            : Format("Trade.ExpiredAcceptorMessage", ("EVENT", tradeOrder.EventId));
        AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            presence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                Format("Trade.ExpiredTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        return notification;
    }

    private AuthoritativeEvent CreateTradeBaselineInvalidatedNotification(
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        IReadOnlyList<string> unavailableDefs,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-baseline-invalidated:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string unavailable = string.Join(", ", unavailableDefs);
        string message = targetIsOwner
            ? Format("Trade.BaselineInvalidatedOwnerMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable))
            : Format("Trade.BaselineInvalidatedAcceptorMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable));
        AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            presence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                Format("Trade.BaselineInvalidatedTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        return notification;
    }

    internal void CancelTradeApplicationAfterLandingFailure(string orderId,
        AuthoritativeEvent root, DateTimeOffset nowUtc, IDictionary<string, AuthoritativeEvent> affected)
    {
        if (!TradeFailureEvidence.TryReadTradeFailureGraph(ledger, root, orderId, out var prepared))
            throw new InvalidOperationException("Trade failure conflicts with its fulfillment or escrow returns.");
        var graph = prepared!;
        foreach (var item in graph.Events)
        {
            var status = item.EventId == orderId ? (graph.AppliedDelivery ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled)
                : graph.Memos.Any(memo => memo.EventId == item.EventId) ? ServerEventStatus.Cancelled
                : item.Status == ServerEventStatus.AppliedToSnapshot ? item.Status
                : graph.AppliedDelivery ? ServerEventStatus.Conflict : ServerEventStatus.Failed;
            affected[item.EventId] = item with { Status = status };
        }

        if (graph.AppliedDelivery) return;
        var returned = graph.OwnerReturn ?? delivery.CreateTradeItemDeliveryEvent("trade-application-failed-owner-return:" + orderId,
            new("server"), graph.Order.Actor, ((TradeEventPayload)graph.Order.Payload).OfferedItems,
            ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn, graph.Order.TargetContext, nowUtc);
        affected[returned.EventId] = returned;
        if (!graph.RefundBuyer) return;
        var exchange = graph.Exchange;
        var paymentReturn = graph.BuyerReturn;
        if (paymentReturn is null)
        {
            string snapshot = snapshots.GetLatest(exchange.Actor.UserId, exchange.Actor.ColonyId!)?.Identity.SnapshotId ?? exchange.AppliedSnapshotId!;
            paymentReturn = delivery.CreateTradeItemDeliveryEvent($"trade-application-failed-acceptor-return:{orderId}:{exchange.EventId}",
                new("server"), exchange.Actor, ((TradeEventPayload)exchange.Payload).RequestedItems, ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn,
                delivery.ResolveTradeTargetContext(exchange.Actor.UserId, exchange.Actor.ColonyId!, snapshot, EventLandingMode.DropPod), nowUtc);
        }

        affected[paymentReturn.EventId] = paymentReturn;
    }
}
