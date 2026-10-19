using AIRsLight.ClashOfRim.Network;

internal static class SessionTestAuthentication
{
    public static string Token(ClashOfRimNetworkState state, string user, string colony)
        => Token(state.LoginSessions, state.AuthTokens, user, colony);

    public static string Token(LoginSessionRegistry sessions, AuthTokenRegistry tokens, string user, string colony)
    {
        var now = DateTimeOffset.UtcNow;
        SnapshotTestAssertions.Check(sessions.TryCreate(user, colony, now, out var session), "Cannot create test session.");
        return tokens.Issue(user, user, colony, session, now);
    }
}
