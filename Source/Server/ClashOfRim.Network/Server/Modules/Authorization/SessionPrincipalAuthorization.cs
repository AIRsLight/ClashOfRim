using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Authorization;

// One authority for token/session availability; domain permissions remain in
// their own policies. Callers preserve this rejection through recovery rechecks.
internal sealed class SessionPrincipalAuthorization(AuthTokenRegistry tokens, LoginSessionRegistry sessions)
{
    internal ProtocolResponse? Authorize(string? token, DateTimeOffset nowUtc, out AuthTokenPrincipal? principal,
        ProtocolErrorCode denialCode = ProtocolErrorCode.Unauthorized, string denialKey = "Auth.Failed")
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token)) return ProtocolResponse.Reject(denialCode, ServerLocalization.Text(denialKey));
        if (!tokens.TryGetPrincipal(token, nowUtc, out var candidate) || candidate is null)
            return ProtocolResponse.Reject(ProtocolErrorCode.SessionUnavailable, ServerLocalization.Text(denialKey));
        if (!sessions.Refresh(candidate.UserId, candidate.ColonyId, candidate.SessionId, nowUtc))
            return ProtocolResponse.Reject(ProtocolErrorCode.SessionUnavailable, ServerLocalization.Text("Auth.SessionExpired"));
        principal = candidate;
        return null;
    }

    internal ProtocolResponse? AuthorizeOwner(string? token, string? userId, string? colonyId, DateTimeOffset nowUtc,
        ProtocolErrorCode denialCode = ProtocolErrorCode.Unauthorized, string denialKey = "Auth.Failed")
    {
        var rejection = Authorize(token, nowUtc, out var principal, denialCode, denialKey);
        return rejection ?? (Matches(principal!, userId, colonyId) ? null : ProtocolResponse.Reject(denialCode, ServerLocalization.Text(denialKey)));
    }

    internal static bool Matches(AuthTokenPrincipal principal, string? userId, string? colonyId) =>
        string.Equals(principal.UserId, userId, StringComparison.Ordinal)
        && string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal);
}
