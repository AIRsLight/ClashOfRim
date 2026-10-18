using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal static class ColonyTombstones
{
    internal static string Key(string userId, string colonyId) => "colony-abandoned:" + userId + ":" + colonyId;
    internal static string NotificationId(string userId, string colonyId) => "colony-tombstone:" + userId + ":" + colonyId;

    internal static bool IsFor(AuthoritativeEvent item, string userId, string colonyId) =>
        item.Type == ServerEventType.ServerNotification && item.Payload is ServerNotificationEventPayload payload
        && payload.NotificationId == NotificationId(userId, colonyId);

    internal static bool IsDeleted(IAuthoritativeEventLedger ledger, EventParty party) =>
        !string.IsNullOrWhiteSpace(party.ColonyId)
        && ledger.FindByIdempotencyKey(Key(party.UserId, party.ColonyId)) is { } tombstone
        && tombstone.Actor.UserId == party.UserId && tombstone.Actor.ColonyId == party.ColonyId
        && tombstone.Target.UserId == "server" && tombstone.Status == ServerEventStatus.AppliedToSnapshot
        && IsFor(tombstone, party.UserId, party.ColonyId);
}
