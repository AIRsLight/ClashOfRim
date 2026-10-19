using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeFailureEvidence;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Trades;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal sealed class EventFailureReports(
    ColonyAccessAuthorization authorization,
    SnapshotMutationCoordinator mutations,
    IAuthoritativeEventLedger ledger,
    TradeLifecycle trades,
    EventNotificationHub notifications,
    OnlinePresenceRegistry presence)
{
    internal ReportEventApplicationFailureResponse Report(ReportEventApplicationFailureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            return new ReportEventApplicationFailureResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.ReportFailureMissingFields")),
                request.EventId ?? string.Empty,
                string.Empty,
                affectedEventCount: 0);
        }

        using (var mutation = mutations.Enter(() =>
            authorization.Authorize(request.AuthToken, request.UserId, request.ColonyId, request.EventId,
                authorizationScope: null, DateTimeOffset.UtcNow)))
        {
            if (mutation.Rejection is { } rejection)
                return new ReportEventApplicationFailureResponse(rejection, request.EventId, string.Empty, 0);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            AuthoritativeEvent? ledgerEvent = ledger.Find(request.EventId);
            if (ledgerEvent is null || !EventVisibility.MatchesParty(FailureReportingParty(ledgerEvent), request.UserId, request.ColonyId))
            {
                return new ReportEventApplicationFailureResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Events.NotFoundOrNotVisible")),
                    request.EventId,
                    string.Empty,
                    affectedEventCount: 0);
            }

            ProtocolResponse? reportValidation = Validate(ledgerEvent, request.SourceEventId);
            if (reportValidation is not null)
                return new ReportEventApplicationFailureResponse(reportValidation, ledgerEvent.EventId,
                    ledgerEvent.Status.ToString(), 0);

            IReadOnlyList<AuthoritativeEvent> notices = EventFailureNotifications.FindFailureNotifications(ledger, ledgerEvent);
            if (ledgerEvent.Status == ServerEventStatus.Failed
                || ledgerEvent.Status == ServerEventStatus.Conflict && notices.Count > 0)
            {
                // Wake durable notification recipients and root parties without recalculating refunds or failure reasons.
                SignalReplay(ledgerEvent, notices);
                return new ReportEventApplicationFailureResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Events.AlreadyFailed")),
                    ledgerEvent.EventId,
                    ledgerEvent.Status.ToString(),
                    affectedEventCount: 0);
            }

            IReadOnlyCollection<AuthoritativeEvent> affected = ApplyFailureGraph(
                ledgerEvent,
                request.Reason,
                nowUtc);
            notifications.SignalUsers(affected
                .SelectMany(evt => new[] { evt.Actor.UserId, evt.Target.UserId })
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.Ordinal));

            AuthoritativeEvent? updatedRoot = ledger.Find(ledgerEvent.EventId);
            return new ReportEventApplicationFailureResponse(
                ProtocolResponse.Ok(ServerText.Format("Events.FailureRecorded")),
                ledgerEvent.EventId,
                updatedRoot?.Status.ToString() ?? ServerEventStatus.Failed.ToString(),
                affected.Count);
        }
    }

    private IReadOnlyCollection<AuthoritativeEvent> ApplyFailureGraph(
        AuthoritativeEvent rootEvent,
        string reason,
        DateTimeOffset nowUtc)
    {
        var before = ledger.ListAll().ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var affected = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);

        if (TryResolveTradeOrderIdFromDelivery(ledger, rootEvent, out string? tradeOrderId))
        {
            trades.CancelTradeApplicationAfterLandingFailure(tradeOrderId!, rootEvent, nowUtc, affected);
        }
        else if (rootEvent.Type == ServerEventType.Trade
            && rootEvent.Payload is TradeEventPayload tradePayload
            && tradePayload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange)
        {
            trades.CancelTradeApplicationAfterLandingFailure(tradePayload.TradeId, rootEvent, nowUtc, affected);
        }
        else
        {
            affected[rootEvent.EventId] = rootEvent with { Status = ServerEventStatus.Failed };
            // A support return or raid loss references an earlier operation, not another failed delivery.
            // The optional client source is an assertion only; it never authorizes closing the parent.
        }

        var events = affected.Values.Where(item => !before.ContainsKey(item.EventId)).ToList();
        events.AddRange(EventFailureNotifications.Create(ledger, presence, rootEvent, affected.Values.ToList(), reason, nowUtc));
        var changes = affected.Values.Where(item => before.ContainsKey(item.EventId))
            .Select(item => new LedgerStatusChange(item.EventId, before[item.EventId].Status, item.Status,
                ExpectedEvent: before[item.EventId])).ToArray();
        ledger.ApplyBatch(new LedgerBatch(events, changes));
        return affected.Values.ToList();
    }

    private static EventParty FailureReportingParty(AuthoritativeEvent item) =>
        item.Payload is TradeEventPayload ? item.Actor : item.Target;

    private ProtocolResponse? Validate(AuthoritativeEvent root, string? sourceId)
    {
        bool supported = root.Type switch
        {
            ServerEventType.ItemDelivery => root.Payload is ItemDeliveryEventPayload,
            ServerEventType.SupportPawn => root.Payload is SupportPawnEventPayload,
            ServerEventType.Raid => root.Payload is RaidEventPayload { AttackerLoss: not null },
            ServerEventType.Trade => root.Payload is TradeEventPayload { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange },
            _ => false
        };
        if (!supported) return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.FailureReportUnsupported"));

        string? tradeId = root.Payload is TradeEventPayload exchange ? exchange.TradeId
            : TryResolveTradeOrderIdFromDelivery(ledger, root, out string? resolved) ? resolved : null;
        bool sourceMatches = string.IsNullOrWhiteSpace(sourceId);
        if (!sourceMatches && tradeId is not null)
        {
            var source = ledger.Find(sourceId!);
            sourceMatches = sourceId == tradeId || source?.Payload is TradeEventPayload
                { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange } related && related.TradeId == tradeId;
        }
        else if (!sourceMatches)
            sourceMatches = root.Payload switch
            {
                SupportPawnEventPayload { ReturnToSender: true } support => sourceId == support.SourceEventId,
                RaidEventPayload { AttackerLoss: { } loss } => sourceId == loss.SourceRaidEventId,
                _ => false
            };
        if (!sourceMatches) return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.FailureReportSourceMismatch"));
        if (root.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Events.FailureReportAlreadyEnded"));

        if (root.Payload is ItemDeliveryEventPayload { IsTradeDelivery: true } && tradeId is null
            || tradeId is not null && !TryReadTradeFailureGraph(ledger, root, tradeId, out _))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade failure conflicts with its fulfillment or escrow returns.");

        // Failed roots can be replayed after their order was closed by the original atomic batch.
        if (tradeId is not null && root.Status != ServerEventStatus.Failed
            && !(root.Status == ServerEventStatus.Conflict && EventFailureNotifications.FindFailureNotifications(ledger, root).Count > 0))
        {
            var order = ledger.Find(tradeId);
            if (order?.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
                || order.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
                return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Events.FailureReportAlreadyEnded"));
        }
        return null;
    }

    private void SignalReplay(AuthoritativeEvent root,
        IReadOnlyList<AuthoritativeEvent> notices) => notifications.SignalUsers(
            notices.Select(item => item.Target.UserId).Concat([root.Actor.UserId, root.Target.UserId])
                .Where(user => !string.IsNullOrWhiteSpace(user) && !string.Equals(user, "server", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal));
}
