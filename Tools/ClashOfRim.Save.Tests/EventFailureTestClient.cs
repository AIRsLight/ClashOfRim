using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventFailureTestClient
{
    public static ReportEventApplicationFailureResponse Report(ClashOfRimNetworkState state, AuthoritativeEvent root,
        string? source = null, string reason = "original failure", string? user = null, string? colony = null, string? token = null)
    {
        user ??= root.Target.UserId; colony ??= root.Target.ColonyId!;
        token ??= Token(state, user, colony);
        var request = new ReportEventApplicationFailureRequest("request-" + reason, root.EventId, user, colony, "current", reason, source, token);
        return state.EventFailureReports.Report(request);
    }
    public static string Token(ClashOfRimNetworkState state, string user, string colony)
    { var now = DateTimeOffset.UtcNow; state.LoginSessions.TryCreate(user, colony, now, out var session); return state.AuthTokens.Issue(user, user, colony, session, now); }
}
