namespace AIRsLight.ClashOfRim.Network;

public sealed class OnlinePresenceRegistry
{
    private static readonly TimeSpan DefaultPresenceTtl = TimeSpan.FromSeconds(75);
    private readonly TimeSpan presenceTtl;
    private readonly object gate = new();
    private readonly Dictionary<string, OnlinePresenceRecord> activeConnections = new(StringComparer.Ordinal);

    public OnlinePresenceRegistry(TimeSpan? presenceTtl = null)
    {
        this.presenceTtl = presenceTtl ?? DefaultPresenceTtl;
    }

    public bool IsUserOnline(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            RemoveExpiredPresence(nowUtc);
            return activeConnections.ContainsKey(userId);
        }
    }

    public IReadOnlyList<string> ListOnlineUsers()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            RemoveExpiredPresence(nowUtc);
            return activeConnections.Keys.ToList();
        }
    }

    public bool ForceDisconnect(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return activeConnections.Remove(userId);
        }
    }

    public bool TryConnectExclusive(string userId, out OnlinePresenceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            RemoveExpiredPresence(nowUtc);
            if (activeConnections.ContainsKey(userId))
            {
                lease = null;
                return false;
            }

            string leaseId = $"presence:{userId}:{Guid.NewGuid():N}";
            activeConnections[userId] = new OnlinePresenceRecord(leaseId, nowUtc + presenceTtl);
            lease = new OnlinePresenceLease(userId, leaseId, Touch, IsActive, Disconnect);
            return true;
        }
    }

    private bool Touch(string userId, string leaseId)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            RemoveExpiredPresence(nowUtc);
            if (!activeConnections.TryGetValue(userId, out OnlinePresenceRecord? record)
                || !string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return false;
            }

            activeConnections[userId] = record with { ExpiresAtUtc = nowUtc + presenceTtl };
            return true;
        }
    }

    private bool IsActive(string userId, string leaseId)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            RemoveExpiredPresence(nowUtc);
            return activeConnections.TryGetValue(userId, out OnlinePresenceRecord? record)
                && string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal);
        }
    }

    private void Disconnect(string userId, string leaseId)
    {
        lock (gate)
        {
            if (activeConnections.TryGetValue(userId, out OnlinePresenceRecord? record)
                && string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal))
            {
                activeConnections.Remove(userId);
            }
        }
    }

    private void RemoveExpiredPresence(DateTimeOffset nowUtc)
    {
        List<string>? expiredUserIds = null;
        foreach (KeyValuePair<string, OnlinePresenceRecord> pair in activeConnections)
        {
            if (pair.Value.ExpiresAtUtc > nowUtc)
            {
                continue;
            }

            expiredUserIds ??= new List<string>();
            expiredUserIds.Add(pair.Key);
        }

        if (expiredUserIds is null)
        {
            return;
        }

        for (int i = 0; i < expiredUserIds.Count; i++)
        {
            activeConnections.Remove(expiredUserIds[i]);
        }
    }

    private sealed record OnlinePresenceRecord(string LeaseId, DateTimeOffset ExpiresAtUtc);
}

public sealed class OnlinePresenceLease : IDisposable
{
    private readonly string userId;
    private readonly string leaseId;
    private readonly Func<string, string, bool> touch;
    private readonly Func<string, string, bool> isActive;
    private readonly Action<string, string> disconnect;
    private bool disposed;

    public OnlinePresenceLease(
        string userId,
        string leaseId,
        Func<string, string, bool> touch,
        Func<string, string, bool> isActive,
        Action<string, string> disconnect)
    {
        this.userId = userId;
        this.leaseId = leaseId;
        this.touch = touch;
        this.isActive = isActive;
        this.disconnect = disconnect;
    }

    public bool Touch()
    {
        return !disposed && touch(userId, leaseId);
    }

    public bool IsActive()
    {
        return !disposed && isActive(userId, leaseId);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disconnect(userId, leaseId);
    }
}
