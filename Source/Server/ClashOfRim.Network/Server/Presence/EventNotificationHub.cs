namespace AIRsLight.ClashOfRim.Network;

public sealed class EventNotificationHub
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly Dictionary<string, long> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<long>>> waiters = new(StringComparer.Ordinal);

    public long GetVersion(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (gate)
        {
            return GetVersionUnderLock(userId);
        }
    }

    public long SignalUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        List<TaskCompletionSource<long>> pending;
        long nextVersion;
        lock (gate)
        {
            nextVersion = GetVersionUnderLock(userId) + 1;
            versions[userId] = nextVersion;

            if (!waiters.Remove(userId, out pending!))
            {
                return nextVersion;
            }
        }

        foreach (TaskCompletionSource<long> waiter in pending)
        {
            waiter.TrySetResult(nextVersion);
        }

        return nextVersion;
    }

    public void SignalUsers(IEnumerable<string> userIds)
    {
        HashSet<string>? seen = null;
        foreach (string userId in userIds)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(userId))
            {
                continue;
            }

            SignalUser(userId);
        }
    }

    public async Task<EventNotificationWaitResult> WaitAsync(
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
        long currentVersion;
        lock (gate)
        {
            currentVersion = GetVersionUnderLock(userId);
            if (currentVersion > knownVersion)
            {
                return new EventNotificationWaitResult(true, currentVersion);
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
                return new EventNotificationWaitResult(true, await waiter.Task.ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new EventNotificationWaitResult(false, GetVersion(userId));
        }
        finally
        {
            RemoveWaiter(userId, waiter);
        }
    }

    private void RemoveWaiter(string userId, TaskCompletionSource<long> waiter)
    {
        lock (gate)
        {
            if (waiters.TryGetValue(userId, out List<TaskCompletionSource<long>>? userWaiters))
            {
                userWaiters.Remove(waiter);
                if (userWaiters.Count == 0)
                {
                    waiters.Remove(userId);
                }
            }

        }
    }

    private long GetVersionUnderLock(string userId)
    {
        return versions.TryGetValue(userId, out long version) ? version : 0;
    }
}

public sealed record EventNotificationWaitResult(bool Changed, long Version);
