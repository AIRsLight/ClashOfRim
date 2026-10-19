using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientShopProtocolTests
{
    private static readonly byte[] Payload = { 0, 127, 128, 255 };
    private static readonly ProtocolMessageKind[] Routes = { ProtocolMessageKind.ListServerShop, ProtocolMessageKind.UpsertServerShopListing,
        ProtocolMessageKind.RemoveServerShopListing, ProtocolMessageKind.PurchaseServerShopListingWithSnapshot };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            foreach (string? token in new[] { null, "", "shop-token" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var f = new Fixture(facade, token);
                foreach (var route in Routes)
                foreach (bool accepted in new[] { false, true })
                {
                    f.Route = route; f.Accepted = accepted;
                    Check((await f.Send()).Success, "Shop transport failed.");
                }
                f.Route = ProtocolMessageKind.UpsertServerShopListing;
                foreach (string kind in new[] { "BuyFromPlayer", "SellToPlayer" })
                foreach (string? mode in new[] { "AtLeast", "AtMost", "atmost", "unknown", null })
                foreach (string? id in new[] { null, "", "listing-é" })
                { f.Kind = kind; f.Mode = mode; f.ListingId = id; Check((await f.Send()).Success, "Shop edit failed."); }
                f.ListingId = "listing-é";
                f.Route = ProtocolMessageKind.PurchaseServerShopListingWithSnapshot;
                foreach (string kind in new[] { "BuyFromPlayer", "SellToPlayer" })
                foreach (bool empty in new[] { false, true })
                {
                    f.Kind = kind; f.EmptyDelivery = empty; f.HttpStatus = HttpStatusCode.ServiceUnavailable;
                    int before = f.Calls; var failed = await f.Send(); string original = f.LastJson!;
                    Check(!failed.Success && failed.ErrorCode == "HttpError" && f.Calls == before + 1, "Shop codec retried a mutation or discarded HTTP failure.");
                    f.HttpStatus = HttpStatusCode.OK; Check((await f.Send()).Success && f.LastJson == original && f.Calls == before + 2, "Retry changed purchase or snapshot metadata.");
                }
                foreach (var route in Routes)
                {
                    f.Route = route; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    int before = f.Calls;
                    Check(!(await f.Send(cancelled.Token)).Success && f.Calls == before, "Cancelled shop command reached HTTP.");
                    f.Malformed = true; Check(!(await f.Send()).Success, "Invalid server JSON was accepted."); f.Malformed = false;
                }
            }
            foreach (bool facade in new[] { false, true })
            {
                using var f = new Fixture(facade, "auth", configured: false);
                foreach (var route in Routes) { f.Route = route; Check((await f.Send()).ErrorCode == "NotConfigured", "Unconfigured shop did not reject locally."); }
                Check(f.Calls == 0 && f.Snapshot.SnapshotUploadKind == "original", "Unconfigured shop changed state or contacted HTTP.");
            }
            var listing = new ModServerShopListingDto { BasePriceSilver = 0, PriceSilver = 10, PriceIncreaseRatio = 1.5, BuyerPurchaseCount = -3 };
            Check(ClientShopRules.TotalPrice(listing, 4) == 82,
                "Legacy client price fallback no longer uses shared pricing.");
            listing.BasePriceSilver = 5; listing.BuyerPurchaseCount = 1;
            Check(ClientShopRules.TotalPrice(listing, 2) == 20, "Client quote used personalized price as base price.");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private sealed class Fixture : HttpMessageHandler
    {
        internal ProtocolMessageKind Route;
        internal bool Accepted = true, EmptyDelivery, Malformed;
        internal string Kind = "SellToPlayer";
        internal string? Mode = "AtLeast", ListingId = "listing-é", LastJson;
        internal int Calls;
        internal HttpStatusCode HttpStatus = HttpStatusCode.OK;
        internal readonly ModSnapshotPackageMetadataDto Snapshot = new() { SnapshotId = "confirmed", SnapshotUploadKind = "original" };
        private readonly ClashOfRimClientNetworkContext context;
        private readonly HttpClient http;
        private readonly ClientShopProtocol protocol;
        private readonly ClashOfRimModNetworkClient? facade;
        private readonly ModThingReferenceDto item = new() { GlobalKey = "item-é", DefName = "Steel", StackCount = 2,
            Metadata = new() { ["complex"] = "state-é" }, ThingPackageId = "package", ThingPackage = new() { GlobalKey = "item-é", Scribe = new() { XmlGzipBase64 = "state", UncompressedBytes = 123 } } };
        internal Fixture(bool legacy, string? token, bool configured = true)
        {
            context = new("http://owner/captured", configured ? "user-é" : "", "colony", "base", null, null, token);
            http = new HttpClient(this, disposeHandler: false);
            protocol = new(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong"), new Environment()), context, () => "not configured");
            if (legacy) facade = new(http, context);
        }
        internal async Task<ClashOfRimClientNetworkResult<object>> Send(CancellationToken token = default)
        {
            switch (Route)
            {
                case ProtocolMessageKind.ListServerShop:
                    var listed = await (facade?.ListServerShopAsync(token) ?? protocol.ListServerShopAsync(token));
                    if (listed.Response is { } list)
                    {
                        VerifyResult(list.Result);
                        Check(list.Result!.Accepted == Accepted && list.Listings.Count == 1, "Shop list protocol result changed.");
                        var row = list.Listings[0]; Check(row.PriceSilver == 40 && row.BasePriceSilver == 10 && row.BuyerPurchaseCount == 2 && row.StockCount == 3
                            && row.Item!.Metadata!["complex"] == "state-é" && row.Item.ThingPackage!.Scribe!.UncompressedBytes == 123, "List lost personalized pricing or complex payload.");
                    }
                    return Result(listed);
                case ProtocolMessageKind.UpsertServerShopListing:
                    var upsert = await (facade?.UpsertServerShopListingAsync(ListingId, Kind, item, 12, 3, 1.25, Mode!, Mode!, token)
                        ?? protocol.UpsertServerShopListingAsync(ListingId, Kind, item, 12, 3, 1.25, Mode!, Mode!, token));
                    if (upsert.Response is { } saved) { VerifyResult(saved.Result); Check(saved.Listing!.ListingId == "listing-é", "Upsert response changed."); }
                    return Result(upsert);
                case ProtocolMessageKind.RemoveServerShopListing:
                    var remove = await (facade?.RemoveServerShopListingAsync("listing-é", token) ?? protocol.RemoveServerShopListingAsync("listing-é", token));
                    if (remove.Response is { } removed) { VerifyResult(removed.Result); Check(removed.Removed == Accepted && removed.ListingId == "listing-é", "Remove response changed."); }
                    return Result(remove);
                default:
                    var delivered = EmptyDelivery ? null : new[] { item };
                    var purchase = await (facade?.PurchaseServerShopListingWithSnapshotAsync("key-é", "listing-é", Kind, 10, 23, 2, delivered, Snapshot, Payload, token)
                        ?? protocol.PurchaseServerShopListingWithSnapshotAsync("key-é", "listing-é", Kind, 10, 23, 2, delivered, Snapshot, Payload, token));
                    if (purchase.Response is { } bought) { VerifyResult(bought.Result); Check(bought.ListingId == "listing-é" && bought.RemainingStockCount == 1
                        && bought.AppliedSnapshotId == "confirmed" && bought.NextLineageToken == "next", "Purchase response lost receipt or snapshot identity."); }
                    return Result(purchase);
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "http://owner/captured/" + ProtocolContractManifest.Find(Route).Route.TrimStart('/'), "Shop route borrowed another server.");
            string json;
            if (Route == ProtocolMessageKind.PurchaseServerShopListingWithSnapshot)
            {
                Check(!request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var credentials)
                    ? string.IsNullOrWhiteSpace(context.AuthToken) : credentials.Single() == context.AuthToken, "Purchase borrowed another principal.");
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(parts.Count == 2 && (await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(Payload), "Snapshot bytes changed.");
                json = await parts["request"].ReadAsStringAsync(token);
            }
            else json = await request.Content!.ReadAsStringAsync(token);
            LastJson = json; using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (Route == ProtocolMessageKind.PurchaseServerShopListingWithSnapshot)
            {
                Check(root.GetProperty("confirmedSnapshot").GetProperty("snapshotUploadKind").GetString() == ModSnapshotUploadKinds.ServerShopPurchaseConfirmation, "Wrong shop snapshot purpose.");
                var purchase = root.GetProperty("purchase"); var buyer = purchase.GetProperty("buyer");
                Check(buyer.GetProperty("userId").GetString() == context.UserId && buyer.GetProperty("colonyId").GetString() == "colony" && buyer.GetProperty("snapshotId").GetString() == "base"
                    && purchase.GetProperty("idempotencyKey").GetString() == "key-é" && purchase.GetProperty("listingId").GetString() == "listing-é"
                    && purchase.GetProperty("listingKind").GetString() == Kind && purchase.GetProperty("unitPriceSilver").GetInt32() == 10
                    && purchase.GetProperty("totalPriceSilver").GetInt32() == 23 && purchase.GetProperty("purchaseCount").GetInt32() == 2, "Purchase intent changed.");
                var things = purchase.GetProperty("deliveredThings"); Check(things.GetArrayLength() == (EmptyDelivery ? 0 : 1), "Delivery null normalization changed.");
                if (!EmptyDelivery) VerifyItem(things[0]);
            }
            else
            {
                Check(root.GetProperty("userId").GetString() == context.UserId && root.GetProperty("colonyId").GetString() == "colony" && root.GetProperty("authToken").GetString() == context.AuthToken, "Shop omitted captured session identity.");
                if (Route == ProtocolMessageKind.RemoveServerShopListing) Check(root.GetProperty("listingId").GetString() == "listing-é", "Remove lost opaque listing id.");
                if (Route == ProtocolMessageKind.UpsertServerShopListing)
                {
                    Check(root.GetProperty("listingId").GetString() == ListingId && root.GetProperty("listingKind").GetString() == Kind
                        && root.GetProperty("priceSilver").GetInt32() == 12 && root.GetProperty("stockCount").GetInt32() == 3
                        && root.GetProperty("priceIncreaseRatio").GetDouble() == 1.25
                        && root.GetProperty("qualityRequirementMode").GetString() == (Mode == "AtMost" ? "AtMost" : "AtLeast")
                        && root.GetProperty("hitPointsRequirementMode").GetString() == (Mode == "AtMost" ? "AtMost" : "AtLeast"), "Upsert listing or mode normalization changed.");
                    VerifyItem(root.GetProperty("item"));
                }
            }
            string result = "\"result\":{\"accepted\":" + (Accepted ? "true" : "false") + ",\"errorCode\":1,\"message\":\"message-é\"}";
            const string listing = "{\"listingId\":\"listing-é\",\"priceSilver\":40,\"basePriceSilver\":10,\"buyerPurchaseCount\":2,\"stockCount\":3,\"item\":{\"metadata\":{\"complex\":\"state-é\"},\"thingPackage\":{\"scribe\":{\"uncompressedBytes\":123}}}}";
            string fields = Route switch {
                ProtocolMessageKind.ListServerShop => "\"listings\":[" + listing + "]",
                ProtocolMessageKind.UpsertServerShopListing => "\"listing\":" + listing,
                ProtocolMessageKind.RemoveServerShopListing => "\"listingId\":\"listing-é\",\"removed\":" + (Accepted ? "true" : "false"),
                _ => "\"listingId\":\"listing-é\",\"remainingStockCount\":1,\"appliedSnapshotId\":\"confirmed\",\"nextLineageToken\":\"next\"" };
            return new(HttpStatus) { Content = new StringContent(Malformed ? "{" : "{" + result + "," + fields + "}", Encoding.UTF8, "application/json") };
        }
        private static void VerifyItem(JsonElement item) => Check(item.GetProperty("stackCount").GetInt32() == 2 && item.GetProperty("metadata").GetProperty("complex").GetString() == "state-é"
            && item.GetProperty("thingPackageId").GetString() == "package" && item.GetProperty("thingPackage").GetProperty("scribe").GetProperty("xmlGzipBase64").GetString() == "state", "Complex item changed on wire.");
        private void VerifyResult(ModProtocolResponseDto? result) => Check(result is not null && result.Accepted == Accepted && result.ErrorCode == 1 && result.Message == "message-é", "Shop protocol result lost acceptance, error code or message.");
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
    private static ClashOfRimClientNetworkResult<object> Result<T>(ClashOfRimClientNetworkResult<T> value) => value.Success
        ? ClashOfRimClientNetworkResult<object>.Ok(value.Response!) : ClashOfRimClientNetworkResult<object>.Failed(value.ErrorCode ?? "", value.Message ?? "");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) { }
    }
}
