using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventConfirmationEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, EventConfirmations confirmations)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplication).Route,
            (HttpRequest request) => ConfirmEventApplication(request, confirmations))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmEventApplications).Route,
            (HttpRequest request) => ConfirmEventApplications(request, confirmations))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> ConfirmEventApplication(HttpRequest httpRequest, EventConfirmations confirmations)
    {
        var multipart = await MultipartSnapshotReader.Read<ConfirmEventApplicationMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
            return Results.Ok(new ConfirmEventApplicationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.ConfirmMissingPayload")),
                eventId: string.Empty, appliedSnapshotId: null, serverValidationResult: "MissingMultipartPayload"));

        return Results.Ok(confirmations.Confirm(multipart.Request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static async Task<IResult> ConfirmEventApplications(HttpRequest httpRequest, EventConfirmations confirmations)
    {
        var multipart = await MultipartSnapshotReader.Read<ConfirmEventApplicationsMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
            return Results.Ok(new ConfirmEventApplicationsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.BatchConfirmMissingPayload")),
                appliedSnapshotId: null, applications: []));

        return Results.Ok(confirmations.Confirm(multipart.Request, multipart.Payload));
    }
}
