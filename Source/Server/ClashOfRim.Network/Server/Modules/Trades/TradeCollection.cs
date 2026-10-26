using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeCollection(IAuthoritativeEventLedger ledger, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, Func<ClashOfRimServerConfiguration> configuration)
{
    internal (AuthoritativeEvent Delivery, AuthoritativeEvent Notice) Prepare(
        AuthoritativeEvent delivery, AuthoritativeEvent order, DateTimeOffset nowUtc)
    {
        TimeSpan expiration = configuration().TradeOrderExpiration;
        DateTimeOffset? deadline = expiration > TimeSpan.Zero ? nowUtc + expiration : null;
        var held = delivery with { Status = ServerEventStatus.AwaitingCollection,
            Payload = ((ItemDeliveryEventPayload)delivery.Payload) with { TradeCollection = new(order.EventId, deadline) } };
        string id = "trade-owner-collection-notice:" + order.EventId;
        var notice = AuthoritativeEventFactory.CreateForLedger(ledger, ServerEventType.ServerNotification,
            new("server"), order.Actor, id, presence.IsUserOnline(order.Actor.UserId),
            new ServerNotificationEventPayload(id, Format("Trade.CollectionReadyTitle"),
                Format(deadline.HasValue ? "Trade.CollectionReadyMessage" : "Trade.CollectionReadyNoDeadlineMessage",
                    ("EVENT", order.EventId), ("DEADLINE", deadline?.ToString("u") ?? "")),
                ServerNotificationSeverity.Info, FromAdministrator: false), nowUtc);
        return (held, notice);
    }

    internal IReadOnlyDictionary<string, AuthoritativeEvent> ReadByOrder() => ledger.ListByType(ServerEventType.ItemDelivery)
        .Where(item => item.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery, TradeCollection: not null })
        .ToDictionary(item => ((ItemDeliveryEventPayload)item.Payload).TradeCollection!.TradeOrderId, StringComparer.Ordinal);

    internal static bool IsOutstanding(AuthoritativeEvent order, AuthoritativeEvent? delivery) =>
        order.Status == ServerEventStatus.AppliedToSnapshot && delivery is not null && delivery.Target == order.Actor
        && delivery.Status != ServerEventStatus.AppliedToSnapshot;

    internal static string Status(AuthoritativeEvent order, AuthoritativeEvent? delivery) => !IsOutstanding(order, delivery)
        ? order.Status.ToString() : delivery!.Status switch
        {
            ServerEventStatus.AwaitingCollection => "Traded",
            ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery or ServerEventStatus.DeliveredToClient => "Collecting",
            _ => delivery.Status.ToString()
        };

    // Callers hold the recovered mutation gate. Manual and timed release share
    // one compare-and-swap of the original event; never create a second delivery.
    internal bool TryRelease(AuthoritativeEvent order, AuthoritativeEvent delivery)
    {
        if (delivery.Target != order.Actor || delivery.Payload is not ItemDeliveryEventPayload { TradeCollection: { } info }
            || info.TradeOrderId != order.EventId || !TryGetCompletedTradeParticipants(ledger, order, out _)) return false;
        if (delivery.Status == ServerEventStatus.AwaitingCollection)
        {
            ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(delivery.EventId, delivery.Status,
                presence.IsUserOnline(delivery.Target.UserId) ? ServerEventStatus.ReadyForImmediateDelivery : ServerEventStatus.PendingOfflineDelivery,
                ExpectedEvent: delivery)]));
        }
        notifications.SignalUsers(new[] { order.Actor.UserId });
        return true;
    }

    internal void ReleaseExpired(DateTimeOffset nowUtc)
    {
        foreach (var entry in ReadByOrder())
        {
            var delivery = entry.Value;
            if (delivery.Status != ServerEventStatus.AwaitingCollection
                || ((ItemDeliveryEventPayload)delivery.Payload).TradeCollection!.ExpiresAtUtc is not { } deadline
                || deadline > nowUtc || ledger.Find(entry.Key) is not { } order) continue;
            TryRelease(order, delivery);
        }
    }
}
