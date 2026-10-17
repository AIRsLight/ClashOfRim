using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

internal static class SupportEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, SupportSending sending, SupportService support)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawn).Route,
            CreateSupportPawn).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateSupportPawnWithSnapshot).Route,
            (HttpRequest http) => CreateSupportPawnWithSnapshot(http, sending)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.FinishSupportPawn).Route,
            (FinishSupportPawnRequest request) => Results.Ok(support.FinishSupportPawn(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RejectSupportPawn).Route,
            (RejectSupportPawnRequest request) => Results.Ok(support.RejectSupportPawn(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> CreateSupportPawnWithSnapshot(HttpRequest http, SupportSending sending)
    {
        var multipart = await MultipartSnapshotReader.Read<CreateSupportPawnWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Support.TransactionMissingPayload")), null, ProtocolDeliverySemantics.OnlineImmediate)
            : sending.CreateWithSnapshot(multipart.Request, multipart.Payload,
                http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static IResult CreateSupportPawn() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Support.SnapshotEndpointRequired")),
        null, ProtocolDeliverySemantics.ServerNotification));
}
