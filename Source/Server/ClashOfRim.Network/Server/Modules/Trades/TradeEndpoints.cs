using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal static class TradeEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, TradeService trades, TradeMarket market)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteTradeOrderFee).Route,
            (QuoteTradeOrderFeeRequest request) => Results.Ok(trades.QuoteTradeOrderFee(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrder).Route,
            CreateTradeOrder).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrderWithSnapshot).Route,
            (HttpRequest http) => CreateTradeOrderWithSnapshot(http, trades)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).Route,
            (ListTradeOrdersRequest request) => Results.Ok(market.ListTradeOrders(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).Route,
            (AcceptTradeOrderRequest request) => Results.Ok(trades.AcceptTradeOrder(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrder).Route,
            FulfillTradeOrder).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrderWithSnapshot).Route,
            (HttpRequest http) => FulfillTradeOrderWithSnapshot(http, trades)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CancelTradeOrder).Route,
            (CloseTradeOrderRequest request) => Results.Ok(trades.CancelTradeOrder(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CompleteTradeOrder).Route,
            (CloseTradeOrderRequest request) => Results.Ok(trades.CompleteTradeOrder(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ClaimTradeOrder).Route,
            (CloseTradeOrderRequest request) => Results.Ok(trades.ClaimTradeOrder(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> CreateTradeOrderWithSnapshot(HttpRequest http, TradeService trades)
    {
        var multipart = await MultipartSnapshotReader.Read<CreateTradeOrderWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Trade.OrderTransactionMissingPayload")), null, ProtocolDeliverySemantics.OnlineImmediate)
            : trades.CreateTradeOrderWithSnapshot(multipart.Request, multipart.Payload, http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static async Task<IResult> FulfillTradeOrderWithSnapshot(HttpRequest http, TradeService trades)
    {
        var multipart = await MultipartSnapshotReader.Read<FulfillTradeOrderWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new FulfillTradeOrderResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Trade.FulfillmentTransactionMissingPayload")), null, null, null, false,
                Array.Empty<ThingReferenceDto>(), Array.Empty<string>(), string.Empty)
            : trades.FulfillTradeOrderWithSnapshot(multipart.Request, multipart.Payload, http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    // Retired requests must not recover or spend escrow.
    internal static IResult CreateTradeOrder() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Trade.SnapshotEndpointRequired")),
        eventId: null, ProtocolDeliverySemantics.ServerNotification));

    internal static IResult FulfillTradeOrder() => Results.Ok(new FulfillTradeOrderResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Trade.SnapshotEndpointRequired")),
        tradeEventId: null, acceptedMemoEventId: null, exchangeEventId: null, exchangeCreated: false,
        Array.Empty<ThingReferenceDto>(), Array.Empty<string>(), tradeStatus: string.Empty));

}
