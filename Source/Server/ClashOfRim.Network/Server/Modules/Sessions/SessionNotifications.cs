using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed record SessionNotificationCursor(string UserId, long Ledger, long Chat, long World, long Players);
internal sealed record SessionStreamMessage(string EventName, object Data);
internal sealed record SessionNotificationBatch(SessionNotificationCursor Cursor, IReadOnlyList<SessionStreamMessage> Messages);

internal sealed class SessionNotifications(EventNotificationHub ledger, EventNotificationHub chat,
    EventNotificationHub world, PlayerOnlineNotificationHub players)
{
    internal long LedgerVersion(string userId) => ledger.GetVersion(userId);

    internal SessionNotificationCursor Capture(string userId, long knownLedger, long knownWorld)
        => new(userId, Math.Max(Math.Max(0, knownLedger), ledger.GetVersion(userId)), chat.GetVersion(userId),
            Math.Max(0, knownWorld), players.GetVersion(userId));

    internal async Task<SessionNotificationBatch> WaitAsync(SessionNotificationCursor cursor, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = waitCancellation.Token;
        var timeout = SessionConnections.HeartbeatInterval;
        var ledgerWait = ledger.WaitAsync(cursor.UserId, cursor.Ledger, timeout, token);
        var chatWait = chat.WaitAsync(cursor.UserId, cursor.Chat, timeout, token);
        var worldWait = world.WaitAsync(cursor.UserId, cursor.World, timeout, token);
        var playerWait = players.WaitAsync(cursor.UserId, cursor.Players, timeout, token);
        Task[] waits = [ledgerWait, chatWait, worldWait, playerWait];
        try
        {
            Task completed = await Task.WhenAny(waits);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == playerWait)
            {
                var result = await playerWait;
                return result.Changed ? new(cursor with { Players = result.Version }, result.Notifications.Select(notification =>
                    new SessionStreamMessage("playerOnline", new PlayerOnlineStreamEventDto(cursor.UserId,
                        notification.OnlineUserId, notification.Version, null))).ToArray()) : new(cursor, []);
            }
            var change = await (Task<EventNotificationWaitResult>)completed;
            if (!change.Changed) return new(cursor, []);
            if (completed == chatWait)
                return new(cursor with { Chat = change.Version }, [new("chatChanged", new ChatStreamEventDto(cursor.UserId, change.Version, ServerLocalization.Text("SessionStream.ChatUpdated")))]);
            if (completed == worldWait)
                return new(cursor with { World = change.Version }, [new("worldConfigurationChanged", new WorldConfigurationStreamEventDto(cursor.UserId, change.Version, ServerLocalization.Text("SessionStream.WorldCatalogUpdated")))]);
            return new(cursor with { Ledger = change.Version }, [new("ledgerChanged", new SessionStreamEventDto(cursor.UserId, true, change.Version, ServerLocalization.Text("SessionStream.EventLedgerUpdated")))]);
        }
        finally
        {
            // Every subscription is owned by this wait. Observe cancellation and remove all losers before returning.
            waitCancellation.Cancel();
            try { await Task.WhenAll(waits); } catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
        }
    }
}
