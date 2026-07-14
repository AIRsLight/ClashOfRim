using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static class MercenaryGuardActivationNotificationFactory
{
    public static AuthoritativeEvent? Create(
        MercenaryGuardContractRecord? consumedContract,
        AuthoritativeEvent raidEvent,
        bool targetOnline,
        string title,
        string message,
        DateTimeOffset nowUtc)
    {
        if (consumedContract is null)
        {
            return null;
        }

        string notificationId = "mercenary-guard-activated:" + raidEvent.EventId;
        return AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            new EventParty(consumedContract.UserId, consumedContract.ColonyId),
            notificationId,
            targetOnline,
            new ServerNotificationEventPayload(
                notificationId,
                title,
                message,
                ServerNotificationSeverity.Info,
                FromAdministrator: false,
                OnlineOnly: false,
                RelatedEventId: raidEvent.EventId,
                RelatedEventType: ServerEventType.Raid,
                RelatedUserId: raidEvent.Actor.UserId,
                RelatedColonyId: raidEvent.Actor.ColonyId),
            nowUtc);
    }
}
