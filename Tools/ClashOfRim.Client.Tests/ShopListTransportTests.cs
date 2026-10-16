using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class ShopListTransportTests
{
    public static void Run()
    {
        foreach (string? token in new[] { null, "", "player-token" })
        foreach (bool accepted in new[] { false, true }) Verify(token, accepted).GetAwaiter().GetResult();
    }
    private static async Task Verify(string? token, bool accepted)
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(token, accepted); using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "player", "colony", "base", null, null, token);
        var client = new ClashOfRimModNetworkClient(http, context);
        var result = (await client.ListServerShopAsync()).Response!;
        if (result.Result!.Accepted != accepted || handler.Calls != 1 || context.AuthToken != token
            || result.Listings.Count != (accepted ? 1 : 0)) throw new Exception("Shop list response or session changed.");
        if (accepted)
        {
            var item = result.Listings[0];
            if (item.ListingId != "listing" || item.BasePriceSilver != 10 || item.PriceSilver != 40 || item.BuyerPurchaseCount != 2 || item.StockCount != 3)
                throw new Exception("Shop list response lost personalized price or stock.");
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly string? token; private readonly bool accepted; public int Calls;
        public Handler(string? token, bool accepted) { this.token = token; this.accepted = accepted; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(ProtocolMessageKind.ListServerShop).Route)
                throw new Exception("Shop list used an unexpected route.");
            var body = JsonSerializer.Deserialize<ListServerShopRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (body.UserId != "player" || body.ColonyId != "colony" || body.AuthToken != token) throw new Exception("Shop list omitted session authentication.");
            string listings = accepted ? "[{\"listingId\":\"listing\",\"basePriceSilver\":10,\"priceSilver\":40,\"buyerPurchaseCount\":2,\"stockCount\":3}]" : "[]";
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":" + (accepted ? "true" : "false") + "},\"listings\":" + listings + "}") };
        }
    }
}
