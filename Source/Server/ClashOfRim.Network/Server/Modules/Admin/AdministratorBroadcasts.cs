using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdministratorBroadcasts(IAuthoritativeEventLedger ledger, PlayerRegistry players,
    OnlinePresenceRegistry presence, EventNotificationHub notifications)
{
    internal int Publish(string actor, string? target, string? message, string? severity, bool persistent, DateTimeOffset at)
    {
        string text = string.IsNullOrWhiteSpace(message) ? Format("Admin.Broadcast.DefaultMessage") : message.Trim();
        var recipients = players.List().Where(player => target is null || player.UserId == target)
            .Where(player => persistent || presence.IsUserOnline(player.UserId)).ToArray();
        if (recipients.Length == 0) return 0;
        var events = recipients.Select(player =>
        {
            string key = $"admin-broadcast:{actor}:{player.UserId}:{at:O}";
            return AuthoritativeEventFactory.CreateForLedger(ledger, ServerEventType.ServerNotification,
                new EventParty("server"), new EventParty(player.UserId, player.ColonyId), key, presence.IsUserOnline(player.UserId),
                new ServerNotificationEventPayload(key, Format("Admin.Broadcast.Title"), text, ParseSeverity(severity),
                    FromAdministrator: true, actor, OnlineOnly: !persistent), at);
        }).ToArray();
        ledger.ApplyBatch(new LedgerBatch(events, []));
        notifications.SignalUsers(recipients.Select(player => player.UserId));
        return recipients.Length;
    }

    private static ServerNotificationSeverity ParseSeverity(string? severity)
        => string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase) ? ServerNotificationSeverity.Warning
            : string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase) || string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)
                ? ServerNotificationSeverity.Critical : ServerNotificationSeverity.Info;
}
