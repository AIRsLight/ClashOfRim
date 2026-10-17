using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeFailureEvidence;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static EventParty FailureReportingParty(AuthoritativeEvent item) =>
        item.Payload is TradeEventPayload ? item.Actor : item.Target;

    private static ProtocolResponse? ValidateFailureReport(ClashOfRimNetworkState state, AuthoritativeEvent root, string? sourceId)
    {
        bool supported = root.Type switch
        {
            ServerEventType.ItemDelivery => root.Payload is ItemDeliveryEventPayload,
            ServerEventType.SupportPawn => root.Payload is SupportPawnEventPayload,
            ServerEventType.Raid => root.Payload is RaidEventPayload { AttackerLoss: not null },
            ServerEventType.Trade => root.Payload is TradeEventPayload { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange },
            _ => false
        };
        if (!supported) return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.FailureReportUnsupported"));

        string? tradeId = root.Payload is TradeEventPayload exchange ? exchange.TradeId
            : TryResolveTradeOrderIdFromDelivery(state.Ledger, root, out string? resolved) ? resolved : null;
        bool sourceMatches = string.IsNullOrWhiteSpace(sourceId);
        if (!sourceMatches && tradeId is not null)
        {
            var source = state.Ledger.Find(sourceId!);
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
        if (!sourceMatches) return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.FailureReportSourceMismatch"));
        if (root.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Events.FailureReportAlreadyEnded"));

        if (root.Payload is ItemDeliveryEventPayload { IsTradeDelivery: true } && tradeId is null
            || tradeId is not null && !Modules.Trades.TradeFailureEvidence.TryReadTradeFailureGraph(state.Ledger, root, tradeId, out _))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, "Trade failure conflicts with its fulfillment or escrow returns.");

        // Failed roots can be replayed after their order was closed by the original atomic batch.
        if (tradeId is not null && root.Status != ServerEventStatus.Failed
            && !(root.Status == ServerEventStatus.Conflict && Modules.Events.EventFailureNotifications.FindFailureNotifications(state.Ledger, root).Count > 0))
        {
            var order = state.Ledger.Find(tradeId);
            if (order?.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder }
                || order.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
                return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Events.FailureReportAlreadyEnded"));
        }
        return null;
    }

    private static void SignalFailureReport(ClashOfRimNetworkState state, AuthoritativeEvent root,
        IReadOnlyList<AuthoritativeEvent> notifications) => state.EventNotifications.SignalUsers(
            notifications.Select(item => item.Target.UserId).Concat([root.Actor.UserId, root.Target.UserId])
                .Where(user => !string.IsNullOrWhiteSpace(user) && !string.Equals(user, "server", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal));
}
