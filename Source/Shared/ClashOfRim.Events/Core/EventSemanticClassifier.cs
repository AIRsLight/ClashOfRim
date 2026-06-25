namespace AIRsLight.ClashOfRim.Events;

public static class EventSemanticClassifier
{
    public static bool RequiresClientApplication(AuthoritativeEvent ledgerEvent)
    {
        ArgumentNullException.ThrowIfNull(ledgerEvent);
        return !IsSnapshotlessNotification(ledgerEvent);
    }

    public static bool IsSnapshotlessNotification(AuthoritativeEvent ledgerEvent)
    {
        ArgumentNullException.ThrowIfNull(ledgerEvent);

        if (ledgerEvent.Type == ServerEventType.ServerNotification
            && ledgerEvent.Payload is ServerNotificationEventPayload)
        {
            return true;
        }

        if (ledgerEvent.Type is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation)
        {
            return true;
        }

        if (ledgerEvent.Type == ServerEventType.Raid
            && ledgerEvent.Payload is RaidEventPayload { Settlement: not null })
        {
            return true;
        }

        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload tradePayload
            && IsSnapshotlessTradeNotification(tradePayload);
    }

    public static bool IsOnlineOnlyServerNotification(AuthoritativeEvent ledgerEvent)
    {
        ArgumentNullException.ThrowIfNull(ledgerEvent);
        return ledgerEvent.Type == ServerEventType.ServerNotification
            && ledgerEvent.Payload is ServerNotificationEventPayload { OnlineOnly: true };
    }

    private static bool IsSnapshotlessTradeNotification(TradeEventPayload tradePayload)
    {
        return tradePayload.Stage is TradeStage.MarketOrder
            or TradeStage.AcceptedMemo
            or TradeStage.Completed;
    }
}
