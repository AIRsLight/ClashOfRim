using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static void ApplyTradeOrderExpirations(ClashOfRimNetworkState state, DateTimeOffset nowUtc)
    {
        using (EnterRecoveredSnapshotMutation(state)) ApplyRecoveredTradeOrderExpirations(state, nowUtc);
    }

    // Authorized callers already own the recovered mutation scope. Do not recover again here.
    private static void ApplyRecoveredTradeOrderExpirations(ClashOfRimNetworkState state, DateTimeOffset nowUtc)
    {
        if (!Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate))
            throw new InvalidOperationException("Trade expiration requires the recovered mutation scope.");
        TimeSpan expiration = state.ServerConfiguration.TradeOrderExpiration;
        if (expiration <= TimeSpan.Zero) return;
        var expiredOrders = state.Ledger.ListByType(ServerEventType.Trade)
            .Where(IsOpenMarketTradeOrderForOwner)
            .Where(order => nowUtc - order.CreatedAtUtc >= expiration).ToArray();
        foreach (var order in expiredOrders)
            ApplyAutomaticTradeCancellation(state, order, TradeCancellationReason.Expired, [], nowUtc);
    }

    private static int ApplyTradeOrderBaselineInvalidations(ClashOfRimNetworkState state,
        AdminBaselineSnapshot baseline, DateTimeOffset nowUtc)
    {
        using (EnterRecoveredSnapshotMutation(state))
        {
            var validDefs = BuildValidTradeThingDefSet(baseline, state.ServerConfiguration);
            if (validDefs.Count == 0) return 0;
            int cancelled = 0;
            foreach (var order in state.Ledger.ListByType(ServerEventType.Trade).Where(IsOpenMarketTradeOrderForOwner).ToArray())
            {
                var payload = (TradeEventPayload)order.Payload;
                var unavailable = FindUnavailableTradeThingDefs(payload.OfferedItems.Concat(payload.RequestedItems), baseline, validDefs);
                if (unavailable.Count > 0 && ApplyAutomaticTradeCancellation(state, order, TradeCancellationReason.BaselineChanged, unavailable, nowUtc))
                    cancelled++;
            }
            return cancelled;
        }
    }

    private static bool ApplyAutomaticTradeCancellation(ClashOfRimNetworkState state, AuthoritativeEvent order,
        TradeCancellationReason reason, IReadOnlyList<string> unavailableDefs, DateTimeOffset nowUtc)
    {
        if (!TryPrepareAutomaticTradeCancellation(state, order, reason, unavailableDefs, nowUtc, out var batch, out var users)) return false;
        try { state.Ledger.ApplyBatch(batch!); }
        catch (InvalidOperationException)
        {
            state.RuntimeLogger.LogWarning("Automatic trade cancellation skipped conflicting evidence: order={OrderId} reason={Reason}", order.EventId, reason);
            return false;
        }
        state.EventNotifications.SignalUsers(users);
        return true;
    }

    private static bool TryPrepareAutomaticTradeCancellation(ClashOfRimNetworkState state, AuthoritativeEvent order,
        TradeCancellationReason reason, IReadOnlyList<string> unavailableDefs, DateTimeOffset nowUtc,
        out LedgerBatch? prepared, out string[] notifyUsers)
    {
        prepared = null;
        notifyUsers = [];
        bool Conflict()
        {
            state.RuntimeLogger.LogWarning("Automatic trade cancellation skipped conflicting evidence: order={OrderId} reason={Reason}", order.EventId, reason);
            return false;
        }
        if (!TryPrepareTradeCancellation(state, order, reason, nowUtc, out var memos, out var batch) || batch is null) return Conflict();
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
                    ? CreateTradeExpiredNotification(state, order, target, owner, nowUtc)
                    : CreateTradeBaselineInvalidatedNotification(state, order, target, owner, unavailableDefs, nowUtc);
                var existing = state.Ledger.FindByIdempotencyKey(notification.IdempotencyKey);
                if (existing is null) events.Add(notification);
                else
                {
                    if (!MatchesAutomaticTradeNotification(existing, notification)) return Conflict();
                    // Preserve an already consumed notification, including its original localized text.
                    changes.Add(new(existing.EventId, existing.Status, existing.Status, ExpectedEvent: existing));
                }
            }
            prepared = new(events, changes);
            LedgerBatchValidation.Validate(prepared, state.Ledger.Find, state.Ledger.FindByIdempotencyKey);
        }
        catch (InvalidOperationException) { return Conflict(); }
        notifyUsers = targets.Select(target => target.UserId).Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static IReadOnlyList<AdminBaselineTradeInvalidation> PrepareTradeOrderBaselineInvalidations(
        ClashOfRimNetworkState state, AdminBaselineSnapshot baseline, DateTimeOffset nowUtc)
    {
        var validDefs = BuildValidTradeThingDefSet(baseline, state.ServerConfiguration);
        var work = new List<AdminBaselineTradeInvalidation>();
        if (validDefs.Count == 0) return work;
        foreach (var order in state.Ledger.ListByType(ServerEventType.Trade).Where(IsOpenMarketTradeOrderForOwner).ToArray())
        {
            var payload = (TradeEventPayload)order.Payload;
            var unavailable = FindUnavailableTradeThingDefs(payload.OfferedItems.Concat(payload.RequestedItems), baseline, validDefs);
            if (unavailable.Count > 0 && TryPrepareAutomaticTradeCancellation(state, order, TradeCancellationReason.BaselineChanged,
                    unavailable, nowUtc, out var batch, out var users))
                work.Add(new(1, order.EventId, batch!, users));
        }
        return work;
    }

    internal static void RecoverAdminBaselineTradeInvalidations(ClashOfRimNetworkState state)
    {
        using (state.EnterSnapshotMutation())
        {
            foreach (var work in state.AdminBaseline.ListPendingTradeInvalidations())
            {
                work.Validate();
                // With no new events, ApplyBatch has no insertion evidence for its normal replay check.
                // Such a batch is complete only if every frozen source is exactly its expected final state.
                bool completedWithoutInsertions = work.Batch.Events.Count == 0 && work.Batch.StatusChanges.All(change =>
                    SameTradeEvidence(state.Ledger.Find(change.EventId), change.ExpectedEvent! with { Status = change.Status }));
                if (!completedWithoutInsertions) state.Ledger.ApplyBatch(work.Batch);
                state.EventNotifications.SignalUsers(work.NotifyUsers);
                state.AdminBaseline.CompleteTradeInvalidation(work.OrderId);
            }
        }
    }

    private static bool MatchesAutomaticTradeNotification(AuthoritativeEvent existing, AuthoritativeEvent expected) =>
        existing.Type == ServerEventType.ServerNotification && existing.Actor == expected.Actor && existing.Target == expected.Target
        && AuthoritativeEventFactory.IsKnownEventId(existing.Type, existing.IdempotencyKey, existing.EventId)
        && existing.Status is not (ServerEventStatus.Failed or ServerEventStatus.Conflict or ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
        && existing.TargetDecision != TargetEventDecision.Rejected && SameTradeEvidence(existing.TargetContext, expected.TargetContext)
        && existing.Payload is ServerNotificationEventPayload payload && expected.Payload is ServerNotificationEventPayload expectedPayload
        && SameTradeEvidence(payload with { Title = expectedPayload.Title, Message = expectedPayload.Message }, expectedPayload);

    private static AuthoritativeEvent CreateTradeExpiredNotification(
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-expired:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string message = targetIsOwner
            ? T("Trade.ExpiredOwnerMessage", ("EVENT", tradeOrder.EventId))
            : T("Trade.ExpiredAcceptorMessage", ("EVENT", tradeOrder.EventId));
        AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            state.OnlinePresence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Trade.ExpiredTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        return notification;
    }

    private static AuthoritativeEvent CreateTradeBaselineInvalidatedNotification(
        ClashOfRimNetworkState state,
        AuthoritativeEvent tradeOrder,
        EventParty target,
        bool targetIsOwner,
        IReadOnlyList<string> unavailableDefs,
        DateTimeOffset nowUtc)
    {
        string notificationId = $"trade-baseline-invalidated:{tradeOrder.EventId}:{target.UserId}:{target.ColonyId}";
        string unavailable = string.Join(", ", unavailableDefs);
        string message = targetIsOwner
            ? T("Trade.BaselineInvalidatedOwnerMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable))
            : T("Trade.BaselineInvalidatedAcceptorMessage", ("EVENT", tradeOrder.EventId), ("ITEMS", unavailable));
        AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            target,
            notificationId,
            state.OnlinePresence.IsUserOnline(target.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Trade.BaselineInvalidatedTitle"),
                message,
                ServerNotificationSeverity.Warning,
                FromAdministrator: false),
            nowUtc);
        return notification;
    }

}
