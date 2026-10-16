using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class ShopAdministrationTransportTests
{
    public static void Run()
    {
        foreach (string operation in new[] { "new-sell", "new-buy", "update-sell", "update-buy", "remove" })
        foreach (bool accepted in new[] { false, true }) Verify(operation, accepted, "admin-token").GetAwaiter().GetResult();
        foreach (string operation in new[] { "new-sell", "remove" }) Verify(operation, false, null).GetAwaiter().GetResult();
    }
    private static async Task Verify(string operation, bool accepted, string? token)
    {
        ClientSessionWork.Reset();
        using var handler = new Handler(operation, accepted, token); using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "admin", "colony", "base", null, null, token);
        var client = new ClashOfRimModNetworkClient(http, context);
        bool actual;
        if (operation == "remove") actual = (await client.RemoveServerShopListingAsync("listing")).Response?.Result?.Accepted == true;
        else actual = (await client.UpsertServerShopListingAsync(operation.StartsWith("new", StringComparison.Ordinal) ? null : "listing",
            operation.EndsWith("buy", StringComparison.Ordinal) ? "BuyFromPlayer" : "SellToPlayer", new ModThingReferenceDto { GlobalKey = "steel", DefName = "Steel", StackCount = 2 },
            12, 3, 1.25, "AtLeast", "AtLeast")).Response?.Result?.Accepted == true;
        if (actual != accepted || handler.Calls != 1 || context.AuthToken != token) throw new Exception("Shop response or session was changed.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly string operation;
        private readonly bool accepted;
        private readonly string? token;
        public Handler(string operation, bool accepted, string? token)
        { this.operation = operation; this.accepted = accepted; this.token = token; }
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            string json = await request.Content!.ReadAsStringAsync(cancellationToken); bool remove = operation == "remove";
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != ProtocolContractManifest.Find(remove ? ProtocolMessageKind.RemoveServerShopListing : ProtocolMessageKind.UpsertServerShopListing).Route)
                throw new Exception("Shop admin request used an unexpected route.");
            if (remove)
            {
                var body = JsonSerializer.Deserialize<RemoveServerShopListingRequest>(json, options)!;
                if (body.UserId != "admin" || body.ColonyId != "colony" || body.AuthToken != token || body.ListingId != "listing") throw new Exception("Removal omitted administrator authentication.");
            }
            else
            {
                var body = JsonSerializer.Deserialize<UpsertServerShopListingRequest>(json, options)!;
                if (body.UserId != "admin" || body.ColonyId != "colony" || body.AuthToken != token
                    || body.ListingId != (operation.StartsWith("new", StringComparison.Ordinal) ? null : "listing")
                    || body.ListingKind != (operation.EndsWith("buy", StringComparison.Ordinal) ? "BuyFromPlayer" : "SellToPlayer")
                    || body.Item.DefName != "Steel" || body.Item.StackCount != 2 || body.PriceSilver != 12 || body.StockCount != 3
                    || body.PriceIncreaseRatio != 1.25 || body.QualityRequirementMode != "AtLeast" || body.HitPointsRequirementMode != "AtLeast")
                    throw new Exception("Upsert omitted authentication or changed listing fields.");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":" + (accepted ? "true" : "false") + "}}") };
        }
    }
}
