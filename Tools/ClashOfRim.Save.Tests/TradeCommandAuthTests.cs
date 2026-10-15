using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

internal static class TradeCommandAuthTests
{
    private static readonly string[] Kinds = ["accept", "cancel", "memo-cancel", "complete"];
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in Kinds)
        {
            foreach (bool replay in new[] { false, true })
            foreach (string invalid in new[] { "missing", "forged", "user", "colony", "ended", "replaced" })
                Guard(kind, replay, invalid);
            RevokedWhileWaiting(kind);
            NullIdentity(kind);
        }
    }

    internal static string Token(ClashOfRimNetworkState state, string user, string? colony = null)
    {
        colony ??= user + "-colony";
        var now = DateTimeOffset.UtcNow;
        Require(state.LoginSessions.TryCreate(user, colony, now, out var session), "cannot create trade session");
        return state.AuthTokens.Issue(user, user, colony, session, now);
    }

    private static (ClashOfRimNetworkState State, AuthoritativeEvent Order, string User, string Token) Setup(string kind)
    {
        var state = new ClashOfRimNetworkState(serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
        var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony", "seller-base"),
            new("server"), "order", false, new TradeEventPayload("order", TradeStage.MarketOrder,
                [new("steel", "Steel", 10, "seller-base")], [new("silver", "Silver", 20, "buyer-base")], 0), DateTimeOffset.UtcNow);
        state.Ledger.Append(order);
        string user = kind is "accept" or "memo-cancel" ? "buyer" : "seller";
        string token = Token(state, user);
        if (kind == "memo-cancel") Require(Call(state, order, "accept", user, token).Accepted, "memo setup failed");
        return (state, order, user, token);
    }

    private static void Guard(string kind, bool replay, string invalid)
    {
        var (state, order, user, valid) = Setup(kind);
        if (replay) Require(Call(state, order, kind, user, valid).Accepted, "valid trade command rejected: " + kind);
        string? token = invalid switch
        {
            "missing" => null,
            "forged" => "forged-token",
            "user" => Token(state, user == "seller" ? "buyer" : "seller"),
            "colony" => Token(state, user, "another-colony"),
            _ => valid
        };
        if (invalid == "ended") state.LoginSessions.EndUser(user);
        if (invalid == "replaced") Token(state, user);
        string before = Snapshot(state);
        var result = Call(state, order, kind, user, token);
        Require(!result.Accepted && result.ErrorCode == ProtocolErrorCode.ServerRejected,
            $"unauthorized trade command accepted: {kind}/{invalid}/replay={replay}");
        Require(before == Snapshot(state), "unauthorized trade command mutated events or notifications");
    }

    private static void RevokedWhileWaiting(string kind)
    {
        var (state, order, user, token) = Setup(kind);
        foreach (bool replaced in new[] { false, true })
        {
            // Each attempt needs a live session before the request starts waiting.
            token = Token(state, user);
            MutationAuthenticationTests.RevokedWhileWaiting(state, user, user + "-colony", replaced,
                () => Call(state, order, kind, user, token).Accepted);
        }
    }

    private static void NullIdentity(string kind)
    {
        var (state, order, user, token) = Setup(kind);
        string before = Snapshot(state);
        Require(!Call(state, order, kind, user, token, nullIdentity: true).Accepted && before == Snapshot(state),
            "missing trade identity was not rejected");
    }

    private static ProtocolResponse Call(ClashOfRimNetworkState state, AuthoritativeEvent order, string kind, string user, string? token, bool nullIdentity = false)
    {
        ProtocolIdentity identity = nullIdentity ? null! : new(user, user + "-colony", user + "-base");
        string method = kind == "accept" ? "AcceptTradeOrder" : kind == "complete" ? "CompleteTradeOrder" : "CancelTradeOrder";
        object request = kind == "accept" ? new AcceptTradeOrderRequest("accept-key", order.EventId, identity, false, token)
            : new CloseTradeOrderRequest("close-key", order.EventId, identity, "结束", token);
        var response = (IValueHttpResult)typeof(ClashOfRimNetworkServer).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request, state])!;
        return response.Value switch
        {
            AcceptTradeOrderResponse accepted => accepted.Result,
            CloseTradeOrderResponse closed => closed.Result,
            _ => throw new Exception("Unexpected trade response")
        };
    }

    private static string Snapshot(ClashOfRimNetworkState state) => JsonSerializer.Serialize(state.Ledger.ListAll())
        + "/" + state.EventNotifications.GetVersion("buyer") + "/" + state.EventNotifications.GetVersion("seller");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
