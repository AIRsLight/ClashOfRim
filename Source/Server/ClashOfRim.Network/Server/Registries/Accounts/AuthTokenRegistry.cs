using System.Security.Cryptography;

namespace AIRsLight.ClashOfRim.Network;

public sealed class AuthTokenRegistry
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(12);
    private readonly object gate = new();
    private readonly Dictionary<string, AuthTokenRecord> tokens = new(StringComparer.Ordinal);

    public string Issue(string steamId, string userId, string colonyId, string sessionId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string token = "auth:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (gate)
        {
            RemoveExpired(nowUtc);
            tokens[token] = new AuthTokenRecord(steamId, userId, colonyId, sessionId, nowUtc + TokenTtl);
        }

        return token;
    }

    public bool IsValid(string? token, string userId, string colonyId, DateTimeOffset nowUtc)
    {
        return TryGetPrincipal(token, nowUtc, out AuthTokenPrincipal? principal)
            && principal is not null
            && string.Equals(principal.UserId, userId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal);
    }

    public bool TryGetPrincipal(string? token, DateTimeOffset nowUtc, out AuthTokenPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (gate)
        {
            RemoveExpired(nowUtc);
            if (!tokens.TryGetValue(token!, out AuthTokenRecord? record) || record.ExpiresAtUtc <= nowUtc)
            {
                return false;
            }

            principal = new AuthTokenPrincipal(record.SteamId, record.UserId, record.ColonyId, record.SessionId);
            return true;
        }
    }

    public void RevokeForColony(string userId, string colonyId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            return;
        }

        RevokeMatching(record => record.UserId == userId && record.ColonyId == colonyId);
    }

    internal void Revoke(string token)
    {
        lock (gate) tokens.Remove(token);
    }

    public void RevokeForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        RevokeMatching(record => record.UserId == userId);
    }

    internal void RevokeForSession(string userId, string sessionId)
        => RevokeMatching(record => record.UserId == userId && record.SessionId == sessionId);

    private void RevokeMatching(Func<AuthTokenRecord, bool> matches)
    {
        lock (gate)
            foreach (string token in tokens.Where(pair => matches(pair.Value)).Select(pair => pair.Key).ToArray())
                tokens.Remove(token);
    }

    private void RemoveExpired(DateTimeOffset nowUtc)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, AuthTokenRecord> pair in tokens)
        {
            if (pair.Value.ExpiresAtUtc <= nowUtc)
            {
                expired ??= new List<string>();
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string token in expired)
        {
            tokens.Remove(token);
        }
    }

    private sealed record AuthTokenRecord(
        string SteamId,
        string UserId,
        string ColonyId,
        string SessionId,
        DateTimeOffset ExpiresAtUtc);
}

public sealed record AuthTokenPrincipal(
    string SteamId,
    string UserId,
    string ColonyId,
    string SessionId);
