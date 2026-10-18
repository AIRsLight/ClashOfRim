namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldConfigurationChanges(PlayerRegistry players, EventNotificationHub notifications)
{
    internal void Signal(params string?[] additionalUserIds)
        => notifications.SignalUsers(players.List().Select(player => player.UserId)
            .Concat(additionalUserIds.Where(userId => !string.IsNullOrWhiteSpace(userId)).Select(userId => userId!)));
}
