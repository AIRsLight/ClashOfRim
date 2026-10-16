namespace AIRsLight.ClashOfRim.Network;

public sealed class LoginSessionRegistry
{
    private static readonly TimeSpan PendingSessionTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveSessionTtl = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly Dictionary<string, LoginSession> sessionsByUser = new(StringComparer.Ordinal);

    internal bool CanCreate(string userId, DateTimeOffset nowUtc)
    {
        lock (gate)
            return !sessionsByUser.TryGetValue(userId, out var existing) || !existing.Streaming || existing.IsExpired(nowUtc);
    }

    internal PreparedSession? Prepare(string userId, string colonyId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        lock (gate)
        {
            sessionsByUser.TryGetValue(userId, out var expected);
            if (expected is { Streaming: true } && !expected.IsExpired(nowUtc)) return null;
            string id = $"session:{userId}:{Guid.NewGuid():N}";
            bool consumed = false;
            bool Publish(DateTimeOffset publishedAt)
            {
                lock (gate)
                {
                    if (consumed) return false;
                    consumed = true;
                    sessionsByUser.TryGetValue(userId, out var current);
                    // Streams and heartbeats can change a pending session outside the server gate.
                    // Never undo their progress, a logout, or another successful login.
                    if (!ReferenceEquals(current, expected)) return false;
                    sessionsByUser[userId] = new LoginSession(userId, colonyId, id, publishedAt, Streaming: false);
                    return true;
                }
            }
            return new(id, Publish);
        }
    }

    internal sealed record PreparedSession(string SessionId, Func<DateTimeOffset, bool> TryPublish);

    public bool TryCreate(string userId, string colonyId, DateTimeOffset nowUtc, out string sessionId)
    {
        lock (gate)
        {
            // Immediate callers use the same admission/publication rules, without an interleaving window.
            var prepared = Prepare(userId, colonyId, nowUtc);
            bool published = prepared?.TryPublish(nowUtc) == true;
            RemoveExpiredSessions(nowUtc);
            sessionId = published ? prepared!.SessionId : string.Empty;
            return published;
        }
    }

    public bool TryBeginStream(string userId, string colonyId, string? sessionId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (gate)
        {
            RemoveExpiredSessions(nowUtc);
            if (!sessionsByUser.TryGetValue(userId, out LoginSession? session)
                || !session.Matches(userId, colonyId, sessionId!)
                || session.Streaming)
            {
                return false;
            }

            sessionsByUser[userId] = session with { LastSeenAtUtc = nowUtc, Streaming = true };
            return true;
        }
    }

    public bool IsValid(string userId, string colonyId, string? sessionId, DateTimeOffset nowUtc)
        => IsValid(userId, colonyId, sessionId, nowUtc, requirePending: false);

    internal bool IsValid(string userId, string colonyId, string? sessionId, DateTimeOffset nowUtc, bool requirePending)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (gate)
        {
            RemoveExpiredSessions(nowUtc);
            return sessionsByUser.TryGetValue(userId, out LoginSession? session)
                && session.Matches(userId, colonyId, sessionId!)
                && (!requirePending || !session.Streaming)
                && !session.IsExpired(nowUtc);
        }
    }

    public bool Refresh(string userId, string colonyId, string? sessionId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (gate)
        {
            RemoveExpiredSessions(nowUtc);
            if (!sessionsByUser.TryGetValue(userId, out LoginSession? session)
                || !session.Matches(userId, colonyId, sessionId!))
            {
                return false;
            }

            sessionsByUser[userId] = session with { LastSeenAtUtc = nowUtc };
            return true;
        }
    }

    public void End(string userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (gate)
        {
            if (sessionsByUser.TryGetValue(userId, out LoginSession? session)
                && string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            {
                sessionsByUser.Remove(userId);
            }
        }
    }

    public bool EndUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return sessionsByUser.Remove(userId);
        }
    }

    private void RemoveExpiredSessions(DateTimeOffset nowUtc)
    {
        List<string>? expiredUserIds = null;
        foreach (KeyValuePair<string, LoginSession> pair in sessionsByUser)
        {
            if (!pair.Value.IsExpired(nowUtc))
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
            sessionsByUser.Remove(expiredUserIds[i]);
        }
    }

    private sealed record LoginSession(
        string UserId,
        string ColonyId,
        string SessionId,
        DateTimeOffset LastSeenAtUtc,
        bool Streaming)
    {
        public bool Matches(string userId, string colonyId, string sessionId)
        {
            return string.Equals(UserId, userId, StringComparison.Ordinal)
                && string.Equals(ColonyId, colonyId, StringComparison.Ordinal)
                && string.Equals(SessionId, sessionId, StringComparison.Ordinal);
        }

        public bool IsExpired(DateTimeOffset nowUtc)
        {
            TimeSpan ttl = Streaming ? ActiveSessionTtl : PendingSessionTtl;
            return LastSeenAtUtc + ttl <= nowUtc;
        }
    }
}
