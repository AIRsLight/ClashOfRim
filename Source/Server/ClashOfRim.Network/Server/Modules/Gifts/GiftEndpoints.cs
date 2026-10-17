using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Gifts;

internal static class GiftEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, GiftService gifts)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGift).Route,
            CreateGift).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateGiftWithSnapshot).Route,
            (HttpRequest http) => CreateGiftWithSnapshot(http, gifts)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RejectGift).Route,
            (RejectGiftRequest request) => Results.Ok(gifts.Reject(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> CreateGiftWithSnapshot(HttpRequest http, GiftService gifts)
    {
        var multipart = await MultipartSnapshotReader.Read<CreateGiftWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Gift.TransactionMissingPayload")), null, ProtocolDeliverySemantics.OnlineImmediate)
            : gifts.CreateWithSnapshot(multipart.Request, multipart.Payload,
                http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    // Deliberately state-free: a retired protocol must not recover or mutate pending work.
    internal static IResult CreateGift() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Gift.SnapshotEndpointRequired")),
        null, ProtocolDeliverySemantics.ServerNotification));
}
