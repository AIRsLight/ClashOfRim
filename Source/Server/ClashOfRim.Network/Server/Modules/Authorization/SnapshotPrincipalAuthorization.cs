namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

internal sealed class SnapshotPrincipalAuthorization(AuthTokenRegistry tokens, LoginSessionRegistry sessions)
{
    internal bool TryAuthorize(string? token, DateTimeOffset nowUtc, out AuthTokenPrincipal? principal)
    {
        return tokens.TryGetPrincipal(token, nowUtc, out principal) && principal is not null
            && sessions.Refresh(principal.UserId, principal.ColonyId, principal.SessionId, nowUtc);
    }

    internal static bool Matches(AuthTokenPrincipal principal, string? userId, string? colonyId) =>
        string.Equals(principal.UserId, userId, StringComparison.Ordinal)
        && string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal);
}
