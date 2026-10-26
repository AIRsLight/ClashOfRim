using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;

internal static class TradeCommandTransportTests
{
    private static readonly ProtocolMessageKind[] Routes = {
        ProtocolMessageKind.QuoteTradeOrderFee, ProtocolMessageKind.CreateTradeOrderWithSnapshot,
        ProtocolMessageKind.ListTradeOrders, ProtocolMessageKind.AcceptTradeOrder,
        ProtocolMessageKind.FulfillTradeOrderWithSnapshot, ProtocolMessageKind.CancelTradeOrder, ProtocolMessageKind.ClaimTradeOrder };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Payload = { 0, 127, 128, 255 };
    private static readonly ClashOfRimClientNetworkContext Owner = new("http://owner/base", "交易者", "colony", "base", null, null, "trade-token");

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        // HTTP error hints use host localization; no game language database is loaded here.
        var harmony = new Harmony("ClashOfRim.Tests.TradeCommandTransport");
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TradeCommandTransportTests), nameof(Text)));
        try
        {
            ClientSessionWork.Reset();
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                using var fixture = new Fixture(facade);
                foreach (var route in Routes)
                {
                    fixture.Route = route;
                    foreach (bool enabled in new[] { false, true })
                    {
                        fixture.Enabled = enabled;
                        var result = await fixture.SendAsync();
                        Require(result.Success, $"Trade codec failed: {route} / {name}.");
                        VerifyResponse(route, result.Response);
                    }
                }
                fixture.Route = ProtocolMessageKind.ListTradeOrders;
                foreach (string scope in new[] { "Open", "Mine", "AcceptedByMe", "History" })
                foreach (bool defaults in new[] { false, true })
                {
                    fixture.Scope = scope;
                    fixture.DefaultPage = defaults;
                    Require((await fixture.SendAsync()).Success, "Market paging failed.");
                }
                fixture.Route = ProtocolMessageKind.CancelTradeOrder;
                foreach (string? reason in new[] { null, "OwnerCancelled", "AcceptorCancelledMemo", "取消 à" })
                {
                    fixture.Reason = reason;
                    VerifyResponse(fixture.Route, (await fixture.SendAsync()).Response);
                }
                // One exchange per call; a caller retry must keep the same command and bytes.
                foreach (var route in new[] { ProtocolMessageKind.CreateTradeOrderWithSnapshot, ProtocolMessageKind.FulfillTradeOrderWithSnapshot })
                {
                    fixture.Route = route;
                    fixture.HttpStatus = HttpStatusCode.ServiceUnavailable;
                    int before = fixture.Calls;
                    Require((await fixture.SendAsync()).ErrorCode == "HttpError" && fixture.Calls == before + 1, "Codec retried a mutation itself.");
                    string original = fixture.LastBody!;
                    fixture.HttpStatus = HttpStatusCode.OK;
                    VerifyResponse(route, (await fixture.SendAsync()).Response);
                    Require(original == fixture.LastBody && fixture.Calls == before + 2, "Retry changed trade metadata.");
                }
                foreach (var route in Routes)
                {
                    fixture.Route = route;
                    fixture.Rejected = true;
                    var rejected = await fixture.SendAsync();
                    Require(rejected.Success && Result(rejected.Response) is { Accepted: false, Message: "拒绝 à" } rejectedProtocol
                        && rejectedProtocol.ErrorCode == (int)ProtocolErrorCode.ServerRejected,
                        "Protocol rejection was lost or confused with a transport failure.");
                    fixture.Rejected = false;
                    int before = fixture.Calls;
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    var response = await fixture.SendAsync(cancelled.Token);
                    Require(!response.Success && fixture.Calls == before, "Cancelled trade reached HTTP.");
                }
            }
            foreach (bool facade in new[] { false, true })
            {
                using var fixture = new Fixture(facade, new("http://owner", "", "colony", "base", null, null, "auth"));
                foreach (var route in Routes)
                {
                    fixture.Route = route;
                    Require((await fixture.SendAsync()).ErrorCode == "NotConfigured", "Unconfigured trade was not rejected locally.");
                }
                Require(fixture.Calls == 0 && fixture.Snapshot.SnapshotUploadKind == "original", "Unconfigured trade sent HTTP or changed metadata.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; harmony.UnpatchAll(harmony.Id); ClientSessionWork.Reset(); }
    }

    private static bool Text(string key, ref string __result) { __result = key; return false; }

    private static ModProtocolResponseDto? Result(object? response) => response switch
    {
        ModTradeOrderFeeQuoteResponseDto r => r.Result, ModEventCreationResponseDto r => r.Result,
        ModListTradeOrdersResponseDto r => r.Result, ModAcceptTradeOrderResponseDto r => r.Result,
        ModFulfillTradeOrderResponseDto r => r.Result, ModCloseTradeOrderResponseDto r => r.Result,
        _ => throw new InvalidOperationException("Missing trade response.")
    };

    private static void VerifyResponse(ProtocolMessageKind route, object? response)
    {
        Require(Result(response)?.Accepted == true, "Trade acceptance was not decoded.");
        bool valid = response switch
        {
            ModTradeOrderFeeQuoteResponseDto r => r.RequiredFeeSilver == 17 && r.MissingMarketValueDefs.SequenceEqual(new[] { "缺价物品" }),
            ModEventCreationResponseDto r => r.EventId == "order" && r.AppliedSnapshotId == "confirmed" && r.NextLineageToken == "next",
            ModListTradeOrdersResponseDto r => !r.TradeMarketplaceEnabled && r.HasMore && r.TotalCount == 40 && r.Offset == 4 && r.Limit == 17
                && r.Orders.Single().ViewerAcceptedMemoEventId == "memo" && r.Orders.Single().ServerDropPodPostage?.PostageSilver == 25,
            ModAcceptTradeOrderResponseDto r => r.TradeEventId == "order" && r.MemoEventId == "memo" && !r.MemoCreated,
            ModCloseTradeOrderResponseDto r => r.TradeEventId == "order" && r.TerminalStatus == (route == ProtocolMessageKind.ClaimTradeOrder ? "Collecting" : "Cancelled") && r.NotifiedAcceptorCount == 3,
            ModFulfillTradeOrderResponseDto r => r.TradeEventId == "order" && r.AcceptedMemoEventId == "memo" && r.ExchangeEventId == "exchange"
                && r.ExchangeCreated && r.TradeStatus == "Completed" && r.OwnerDeliveryEventId == "owner-delivery" && r.AcceptorDeliveryEventId == "acceptor-delivery"
                && r.AppliedSnapshotId == "confirmed" && r.NextLineageToken == "next" && r.ReceivedThings.Single().DisplayLabel == "复杂物品 à"
                && r.MissingRequirements.SequenceEqual(new[] { "Steel" }),
            _ => false
        };
        Require(valid, $"Trade response lost domain data: {route}.");
    }

    private sealed record Reply(bool Success, string? ErrorCode, object? Response);
    private static async Task<Reply> Read<T>(Task<ClashOfRimClientNetworkResult<T>> task)
    { var r = await task; return new(r.Success, r.ErrorCode, r.Response); }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly bool facade;
        private readonly ClashOfRimClientNetworkContext context;
        private readonly HttpClient http;
        private readonly ClientTradeProtocol protocol;
        private readonly ClashOfRimModNetworkClient client;
        internal ProtocolMessageKind Route;
        internal bool Enabled, DefaultPage, Rejected;
        internal string Scope = "Open";
        internal string? Reason = "OwnerCancelled";
        internal HttpStatusCode HttpStatus = HttpStatusCode.OK;
        internal int Calls;
        internal string? LastBody;
        internal readonly ModSnapshotPackageMetadataDto Snapshot = new() { OwnerId = "交易者", ColonyId = "colony", SnapshotId = "confirmed",
            PreviousSnapshotId = "base", LineageToken = "lineage", SnapshotUploadKind = "original", GameTicks = 123456789, PayloadBytes = Payload.Length };
        private readonly ModThingReferenceDto[] offered = { new() { GlobalKey = "物品", DefName = "MinifiedThing", StackCount = 2, MarketValue = 1234.5f,
            DisplayLabel = "复杂物品 à", MinifiedInnerDefName = "SculptureLarge", MinifiedInnerQuality = "Masterwork",
            ThingPackageId = "thing-package", PawnPackageId = "pawn-package", UniqueWeaponTraits = new() { "trait" },
            Metadata = new() { ["文化"] = "é" }, ThingPackage = new() { GlobalKey = "物品", Fingerprint = "fp", Scribe = new() { XmlGzipBase64 = "eA==", XmlSha256 = "hash" } } } };
        private readonly ModThingReferenceDto[] requested = { new() { DefName = "Steel", StackCount = 7, MarketValue = 1.9f } };

        internal Fixture(bool facade, ClashOfRimClientNetworkContext? context = null)
        {
            this.facade = facade; this.context = context ?? Owner;
            http = new HttpClient(this, disposeHandler: false);
            protocol = new ClientTradeProtocol(new ClientProtocolTransport(http,
                new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong-token"), new Environment()), () => "not configured");
            client = new ClashOfRimModNetworkClient(http, this.context);
        }

        internal Task<Reply> SendAsync(CancellationToken token = default) => Route switch
        {
            ProtocolMessageKind.QuoteTradeOrderFee => Read(facade ? client.QuoteTradeOrderFeeAsync(offered, requested, token)
                : protocol.QuoteTradeOrderFeeAsync(context, offered, requested, token)),
            ProtocolMessageKind.CreateTradeOrderWithSnapshot => Read(facade ? client.CreateTradeOrderWithSnapshotAsync("key", offered, requested, 17, Enabled, !Enabled, Snapshot, Payload, token)
                : protocol.CreateTradeOrderWithSnapshotAsync(context, "key", offered, requested, 17, Enabled, !Enabled, Snapshot, Payload, token)),
            ProtocolMessageKind.ListTradeOrders => Read(facade ? client.ListTradeOrdersAsync(Scope, DefaultPage ? -3 : 4, DefaultPage ? 0 : 17, token)
                : protocol.ListTradeOrdersAsync(context, Scope, DefaultPage ? -3 : 4, DefaultPage ? 0 : 17, token)),
            ProtocolMessageKind.AcceptTradeOrder => Read(facade ? client.AcceptTradeOrderAsync("key", "order", Enabled, token)
                : protocol.AcceptTradeOrderAsync(context, "key", "order", Enabled, token)),
            ProtocolMessageKind.FulfillTradeOrderWithSnapshot => Read(facade ? client.FulfillTradeOrderWithSnapshotAsync("key", "order", "memo", offered, Enabled ? "ServerDropPod" : "SelfDelivery", Snapshot, Payload, token)
                : protocol.FulfillTradeOrderWithSnapshotAsync(context, "key", "order", "memo", offered, Enabled ? "ServerDropPod" : "SelfDelivery", Snapshot, Payload, token)),
            ProtocolMessageKind.CancelTradeOrder => Read(facade ? client.CancelTradeOrderAsync("key", "order", Reason, token)
                : protocol.CancelTradeOrderAsync(context, "key", "order", Reason, token)),
            ProtocolMessageKind.ClaimTradeOrder => Read(facade ? client.ClaimTradeOrderAsync("key", "order", token)
                : protocol.ClaimTradeOrderAsync(context, "key", "order", token)),
            _ => throw new InvalidOperationException("Unsupported test route.")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "http://owner/base/" + ProtocolContractManifest.Find(Route).Route.TrimStart('/'),
                "Trade borrowed the transport's other server or used the wrong route.");
            if (!facade) Require(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader("简体中文"), "Trade language header changed.");
            bool snapshot = Route is ProtocolMessageKind.CreateTradeOrderWithSnapshot or ProtocolMessageKind.FulfillTradeOrderWithSnapshot;
            string json;
            if (snapshot)
            {
                Require(request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == Owner.AuthToken, "Trade snapshot borrowed another credential.");
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Require(parts.Count == 2 && parts["payload"].Headers.ContentDisposition!.FileName!.Trim('"') == "snapshot.payload"
                    && (await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(Payload), "Trade snapshot bytes or multipart names changed.");
                json = await parts["request"].ReadAsStringAsync(token);
            }
            else json = await request.Content!.ReadAsStringAsync(token);
            LastBody = json;
            VerifyRequest(json);
            if (HttpStatus != HttpStatusCode.OK) return new(HttpStatus) { Content = new StringContent("retry") };
            string result = Rejected ? "\"accepted\":false,\"errorCode\":" + (int)ProtocolErrorCode.ServerRejected + ",\"message\":\"拒绝 à\"" : "\"accepted\":true";
            string fields = Route switch
            {
                ProtocolMessageKind.QuoteTradeOrderFee => "\"requiredFeeSilver\":17,\"missingMarketValueDefs\":[\"缺价物品\"]",
                ProtocolMessageKind.CreateTradeOrderWithSnapshot => "\"eventId\":\"order\",\"appliedSnapshotId\":\"confirmed\",\"nextLineageToken\":\"next\"",
                ProtocolMessageKind.ListTradeOrders => "\"tradeMarketplaceEnabled\":false,\"hasMore\":true,\"totalCount\":40,\"offset\":4,\"limit\":17,\"orders\":[{\"eventId\":\"order\",\"viewerAcceptedMemoEventId\":\"memo\",\"serverDropPodPostage\":{\"postageSilver\":25}}]",
                ProtocolMessageKind.AcceptTradeOrder => "\"tradeEventId\":\"order\",\"memoEventId\":\"memo\",\"memoCreated\":false",
                ProtocolMessageKind.CancelTradeOrder => "\"tradeEventId\":\"order\",\"terminalStatus\":\"Cancelled\",\"notifiedAcceptorCount\":3",
                ProtocolMessageKind.ClaimTradeOrder => "\"tradeEventId\":\"order\",\"terminalStatus\":\"Collecting\",\"notifiedAcceptorCount\":3",
                _ => "\"tradeEventId\":\"order\",\"acceptedMemoEventId\":\"memo\",\"exchangeEventId\":\"exchange\",\"exchangeCreated\":true,\"tradeStatus\":\"Completed\",\"ownerDeliveryEventId\":\"owner-delivery\",\"acceptorDeliveryEventId\":\"acceptor-delivery\",\"appliedSnapshotId\":\"confirmed\",\"nextLineageToken\":\"next\",\"receivedThings\":[{\"displayLabel\":\"复杂物品 à\"}],\"missingRequirements\":[\"Steel\"]"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{" + result + "}," + fields + "}", Encoding.UTF8, "application/json") };
        }

        private void VerifyRequest(string json)
        {
            switch (Route)
            {
                case ProtocolMessageKind.QuoteTradeOrderFee:
                    var quote = Decode<QuoteTradeOrderFeeRequest>(json); Identity(quote.Owner); Things(quote.OfferedThings, quote.RequestedThings); break;
                case ProtocolMessageKind.CreateTradeOrderWithSnapshot:
                    var create = Decode<CreateTradeOrderWithSnapshotRequest>(json); var order = create.TradeOrder;
                    Identity(order.Owner); Things(order.OfferedThings, order.RequestedThings);
                    Require(order.IdempotencyKey == "key" && order.FeeSilver == 17 && order.AllowSelfPickup == Enabled && order.AllowServerDropPod == !Enabled, "Trade creation options changed.");
                    SnapshotMetadata(create.ConfirmedSnapshot, ModSnapshotUploadKinds.TradeOrderCreationConfirmation); break;
                case ProtocolMessageKind.ListTradeOrders:
                    var list = Decode<ListTradeOrdersRequest>(json);
                    Require(list.UserId == Owner.UserId && list.ColonyId == Owner.ColonyId && list.CurrentSnapshotId == Owner.CurrentSnapshotId && list.AuthToken == Owner.AuthToken
                        && list.Scope == Scope && list.Offset == (DefaultPage ? 0 : 4) && list.Limit == (DefaultPage ? 10 : 17), "Market identity, scope or paging changed."); break;
                case ProtocolMessageKind.AcceptTradeOrder:
                    var accept = Decode<AcceptTradeOrderRequest>(json); Identity(accept.Acceptor);
                    Require(accept.AuthToken == Owner.AuthToken && accept.IdempotencyKey == "key" && accept.TradeEventId == "order" && accept.PostagePaidByAcceptor == Enabled, "Accept authorization or postage changed."); break;
                case ProtocolMessageKind.FulfillTradeOrderWithSnapshot:
                    var fulfill = Decode<FulfillTradeOrderWithSnapshotRequest>(json); var command = fulfill.Fulfillment;
                    Identity(command.Acceptor); Things(command.DeliveredThings);
                    Require(command.IdempotencyKey == "key" && command.TradeEventId == "order" && command.AcceptedMemoEventId == "memo"
                        && command.FulfillmentMode == (Enabled ? "ServerDropPod" : "SelfDelivery"), "Fulfillment identity, memo or mode changed.");
                    SnapshotMetadata(fulfill.ConfirmedSnapshot, ModSnapshotUploadKinds.TradeFulfillmentConfirmation); break;
                case ProtocolMessageKind.CancelTradeOrder:
                case ProtocolMessageKind.ClaimTradeOrder:
                    var cancel = Decode<CloseTradeOrderRequest>(json); Identity(cancel.Owner);
                    Require(cancel.AuthToken == Owner.AuthToken && cancel.IdempotencyKey == "key" && cancel.TradeEventId == "order"
                        && cancel.Reason == (Route == ProtocolMessageKind.ClaimTradeOrder ? null : Reason), "Order command authorization or intent changed."); break;
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }

    private static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    private static void Identity(ProtocolIdentity identity) => Require(identity.UserId == Owner.UserId && identity.ColonyId == Owner.ColonyId && identity.SnapshotId == Owner.CurrentSnapshotId, "Trade lost captured identity.");
    private static void Things(IReadOnlyList<ThingReferenceDto> things, IReadOnlyList<ThingReferenceDto>? requirements = null)
    {
        var item = things.Single();
        Require(item.GlobalKey == "物品" && item.DefName == "MinifiedThing" && item.StackCount == 2 && item.MarketValue == 1234.5f && item.DisplayLabel == "复杂物品 à"
            && item.MinifiedInnerDefName == "SculptureLarge" && item.MinifiedInnerQuality == "Masterwork" && item.ThingPackageId == "thing-package" && item.PawnPackageId == "pawn-package"
            && item.UniqueWeaponTraits.SequenceEqual(new[] { "trait" }) && item.Metadata["文化"] == "é" && item.ThingPackage?.Scribe?.XmlGzipBase64 == "eA=="
            && item.ThingPackage.Fingerprint == "fp", "Trade lost complex item data or culture-independent numbers.");
        if (requirements is not null) Require(requirements.Single().DefName == "Steel" && requirements.Single().StackCount == 7 && requirements.Single().MarketValue == 1.9f, "Trade requirements changed.");
    }
    private static void SnapshotMetadata(SnapshotPackageMetadataDto snapshot, string kind) => Require(snapshot.OwnerId == Owner.UserId && snapshot.ColonyId == Owner.ColonyId
        && snapshot.SnapshotId == "confirmed" && snapshot.PreviousSnapshotId == "base" && snapshot.LineageToken == "lineage" && snapshot.SnapshotUploadKind == kind
        && snapshot.GameTicks == 123456789 && snapshot.PayloadBytes == Payload.Length, "Trade snapshot identity, lineage or purpose changed.");
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => "简体中文";
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) { }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
