namespace AIRsLight.ClashOfRim.Network;

public sealed class PlayerOnlineNotificationHub
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly Dictionary<string, long> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PlayerOnlineNotificationRecord>> notifications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<long>>> waiters = new(StringComparer.Ordinal);

    public long GetVersion(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (gate)
        {
            return GetVersionUnderLock(userId);
        }
    }

    public void SignalPlayerOnline(string onlineUserId, IEnumerable<string> targetUserIds, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(onlineUserId))
        {
            return;
        }

        foreach (string targetUserId in targetUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, onlineUserId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal))
        {
            SignalUserUnderLock(targetUserId, onlineUserId, occurredAtUtc);
        }
    }

    public async Task<PlayerOnlineNotificationWaitResult> WaitAsync(
        string userId,
        long knownVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        TimeSpan effectiveTimeout = timeout <= TimeSpan.Zero || timeout > MaximumWait
            ? MaximumWait
            : timeout;

        TaskCompletionSource<long> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            long currentVersion = GetVersionUnderLock(userId);
            if (currentVersion > knownVersion)
            {
                return BuildResultUnderLock(userId, knownVersion);
            }

            if (!waiters.TryGetValue(userId, out List<TaskCompletionSource<long>>? userWaiters))
            {
                userWaiters = new List<TaskCompletionSource<long>>();
                waiters[userId] = userWaiters;
            }

            userWaiters.Add(waiter);
        }

        try
        {
            Task delay = Task.Delay(effectiveTimeout, cancellationToken);
            Task completed = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
            if (completed == waiter.Task)
            {
                await waiter.Task.ConfigureAwait(false);
                lock (gate)
                {
                    return BuildResultUnderLock(userId, knownVersion);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return new PlayerOnlineNotificationWaitResult(false, GetVersionUnderLock(userId), Array.Empty<PlayerOnlineNotificationRecord>());
            }
        }
        finally
        {
            RemoveWaiter(userId, waiter);
        }
    }

    private void SignalUserUnderLock(string targetUserId, string onlineUserId, DateTimeOffset occurredAtUtc)
    {
        List<TaskCompletionSource<long>> pending;
        long nextVersion;
        lock (gate)
        {
            nextVersion = GetVersionUnderLock(targetUserId) + 1;
            versions[targetUserId] = nextVersion;
            if (!notifications.TryGetValue(targetUserId, out List<PlayerOnlineNotificationRecord>? targetNotifications))
            {
                targetNotifications = new List<PlayerOnlineNotificationRecord>();
                notifications[targetUserId] = targetNotifications;
            }

            targetNotifications.Add(new PlayerOnlineNotificationRecord(nextVersion, onlineUserId, occurredAtUtc));
            if (targetNotifications.Count > 20)
            {
                targetNotifications.RemoveRange(0, targetNotifications.Count - 20);
            }

            if (!waiters.Remove(targetUserId, out pending!))
            {
                return;
            }
        }

        foreach (TaskCompletionSource<long> waiter in pending)
        {
            waiter.TrySetResult(nextVersion);
        }
    }

    private PlayerOnlineNotificationWaitResult BuildResultUnderLock(string userId, long knownVersion)
    {
        long currentVersion = GetVersionUnderLock(userId);
        IReadOnlyList<PlayerOnlineNotificationRecord> visible = notifications.TryGetValue(userId, out List<PlayerOnlineNotificationRecord>? targetNotifications)
            ? targetNotifications.Where(notification => notification.Version > knownVersion).ToList()
            : Array.Empty<PlayerOnlineNotificationRecord>();
        return new PlayerOnlineNotificationWaitResult(visible.Count > 0, currentVersion, visible);
    }

    private void RemoveWaiter(string userId, TaskCompletionSource<long> waiter)
    {
        lock (gate)
        {
            if (!waiters.TryGetValue(userId, out List<TaskCompletionSource<long>>? userWaiters))
            {
                return;
            }

            userWaiters.Remove(waiter);
            if (userWaiters.Count == 0)
            {
                waiters.Remove(userId);
            }
        }
    }

    private long GetVersionUnderLock(string userId)
    {
        return versions.TryGetValue(userId, out long version) ? version : 0;
    }
}

public sealed record PlayerOnlineNotificationRecord(long Version, string OnlineUserId, DateTimeOffset OccurredAtUtc);

public sealed record PlayerOnlineNotificationWaitResult(
    bool Changed,
    long Version,
    IReadOnlyList<PlayerOnlineNotificationRecord> Notifications);
