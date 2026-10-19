using AIRsLight.ClashOfRim.Network;

internal static class SessionTestAuthentication
{
    public static string Token(ClashOfRimNetworkState state, string user, string colony)
    {
        var now = DateTimeOffset.UtcNow;
        SnapshotTestAssertions.Check(state.LoginSessions.TryCreate(user, colony, now, out var session), "Cannot create test session.");
        return state.AuthTokens.Issue(user, user, colony, session, now);
    }
}
