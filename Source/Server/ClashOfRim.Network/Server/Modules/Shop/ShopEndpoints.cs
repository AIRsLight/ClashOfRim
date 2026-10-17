using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Shop;

internal static class ShopEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static void Map(IEndpointRouteBuilder endpoints, ShopService shop)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ListServerShop).Route,
            (ListServerShopRequest request) => Results.Ok(shop.List(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UpsertServerShopListing).Route,
            (HttpRequest http) => Upsert(http, shop)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RemoveServerShopListing).Route,
            (RemoveServerShopListingRequest request) => Results.Ok(shop.Remove(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PurchaseServerShopListingWithSnapshot).Route,
            (HttpRequest http) => PurchaseServerShopListingWithSnapshot(http, shop)).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> Upsert(HttpRequest http, ShopService shop)
    {
        UpsertServerShopListingRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<UpsertServerShopListingRequest>(http.Body, JsonOptions); }
        catch (JsonException) { request = null; }
        catch (NotSupportedException) { request = null; }
        return Results.Ok(request is null
            ? new UpsertServerShopListingResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Shop.InvalidItem")), null)
            : shop.Upsert(request));
    }

    internal static async Task<IResult> PurchaseServerShopListingWithSnapshot(HttpRequest http, ShopService shop)
    {
        var multipart = await MultipartSnapshotReader.Read<PurchaseServerShopListingWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new PurchaseServerShopListingResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Shop.PurchaseMissingPayload")), null, 0)
            : shop.Purchase(multipart.Request, multipart.Payload, http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }
}
