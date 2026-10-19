using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Modules.Shop;
using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Text.Json;

internal static class SnapshotEndpointTestClient
{
    public static void LoadLocalization() => ClashOfRim.TestSupport.ServerTestLocalization.LoadEnglish();

    public static Task<T> Invoke<T>(string name, ClashOfRimNetworkState state, object request, byte[] payload, ProtocolIdentity principal)
        => Invoke<T>(name, state, _ => request, payload, principal);

    public static async Task<T> Invoke<T>(string name, ClashOfRimNetworkState state, Func<string, object> requestFactory,
        byte[] payload, ProtocolIdentity principal, string? authToken = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token;
        if (authToken is not null) token = authToken;
        else
        {
            state.LoginSessions.TryCreate(principal.UserId, principal.ColonyId!, now, out string sessionId);
            token = state.AuthTokens.Issue(principal.UserId, principal.UserId, principal.ColonyId!, sessionId, now);
        }
        object request = requestFactory(token);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(request)), "request");
        multipart.Add(new ByteArrayContent(payload), "payload", "snapshot.gz");
        await using var body = new MemoryStream(await multipart.ReadAsByteArrayAsync());
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Headers[MultipartSnapshotAuthentication.HeaderName] = token;
        if (!MultipartSnapshotAuthentication.TryAuthorize(context.Request, state, now))
            throw new Exception("Fixture authentication failed.");
        IResult result;
        if (name == nameof(EventConfirmationEndpoints.ConfirmEventApplication))
            result = await EventConfirmationEndpoints.ConfirmEventApplication(context.Request, state.EventConfirmations);
        else if (name == nameof(EventConfirmationEndpoints.ConfirmEventApplications))
            result = await EventConfirmationEndpoints.ConfirmEventApplications(context.Request, state.EventConfirmations);
        else if (name == nameof(BankEndpoints.CreateBankLoanWithSnapshot))
            result = await BankEndpoints.CreateBankLoanWithSnapshot(context.Request, state.Bank);
        else if (name == nameof(BankEndpoints.RepayBankLoanWithSnapshot))
            result = await BankEndpoints.RepayBankLoanWithSnapshot(context.Request, state.Bank);
        else if (name == nameof(BankEndpoints.RepayBankDebtWithSnapshot))
            result = await BankEndpoints.RepayBankDebtWithSnapshot(context.Request, state.Bank);
        else if (name == nameof(GiftEndpoints.CreateGiftWithSnapshot))
            result = await GiftEndpoints.CreateGiftWithSnapshot(context.Request, state.Gifts);
        else if (name == nameof(TradeEndpoints.CreateTradeOrderWithSnapshot))
            result = await TradeEndpoints.CreateTradeOrderWithSnapshot(context.Request, state.Trades);
        else if (name == nameof(TradeEndpoints.FulfillTradeOrderWithSnapshot))
            result = await TradeEndpoints.FulfillTradeOrderWithSnapshot(context.Request, state.Trades);
        else if (name == nameof(ShopEndpoints.PurchaseServerShopListingWithSnapshot))
            result = await ShopEndpoints.PurchaseServerShopListingWithSnapshot(context.Request, state.Shop);
        else if (name == nameof(MercenaryEndpoints.HireMercenaryWithSnapshot))
            result = await MercenaryEndpoints.HireMercenaryWithSnapshot(context.Request, state.MercenaryHiring);
        else if (name == nameof(MercenaryEndpoints.HireMercenaryGuardWithSnapshot))
            result = await MercenaryEndpoints.HireMercenaryGuardWithSnapshot(context.Request, state.MercenaryHiring);
        else if (name == nameof(SupportEndpoints.CreateSupportPawnWithSnapshot))
            result = await SupportEndpoints.CreateSupportPawnWithSnapshot(context.Request, state.SupportSending);
        else if (name == nameof(RaidEndpoints.CreateRaidWithSnapshot))
            result = await RaidEndpoints.CreateRaidWithSnapshot(context.Request, state.RaidStarting);
        else
        {
            MethodInfo endpoint = typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
            result = await (Task<IResult>)endpoint.Invoke(null, new object[] { context.Request, state })!;
        }
        return (T)((IValueHttpResult)result).Value!;
    }
}
