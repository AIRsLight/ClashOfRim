using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Protocol;
using static SupportClosureTestData;
using static SnapshotTestAssertions;

internal sealed class SupportClosureTestClient(SupportService support, IAuthoritativeEventLedger ledger,
    LoginSessionRegistry sessions, AuthTokenRegistry authentication)
{
    private readonly Dictionary<(string User, string Colony), string> tokens = new();

    public static SupportClosureTestClient Create(ClashOfRimNetworkState state)
        => new(state.Support, state.Ledger, state.LoginSessions, state.AuthTokens);

    // Credentials belong to this client instance. A reopened fixture creates a new client and sessions.
    public string Token(string user, string colony = "colonyA")
    {
        lock (tokens)
        {
            var key = (user, colony);
            if (tokens.TryGetValue(key, out var token)) return token;
            return tokens[key] = SessionTestAuthentication.Token(sessions, authentication, user, colony);
        }
    }

    public (bool Accepted, bool Created, string? ReturnId) Call(string kind,
        string reason = "original", string user = "userA", string pawnKey = PawnKey, string packageKey = PawnKey, string eventId = "source",
        string? token = null, bool authenticate = true)
    {
        if (authenticate) token = Token(user);
        if (kind == "reject")
        {
            var response = support.RejectSupportPawn(new RejectSupportPawnRequest(eventId, user, "colonyA", "current", reason, token));
            return (response.Result.Accepted, response.ReturnEventCreated, response.ReturnEventId);
        }
        var finish = support.FinishSupportPawn(Request(kind, reason, user, pawnKey, packageKey, eventId, token));
        if (kind == "loss" && finish.Result.Accepted && finish.Created)
            Check(((SupportPawnEventPayload)ledger.Find(finish.ReturnEventId!)!.Payload).PawnReference?.Dead == true,
                "Loss return did not explicitly identify the dead pawn independently of its reason.");
        return (finish.Result.Accepted, finish.Created, finish.ReturnEventId);
    }
}
