using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, RaidPreparation preparation, RaidStarting starting)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareRaid).Route,
            (PrepareRaidRequest request) => Results.Ok(preparation.Prepare(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaid).Route,
            CreateRaid).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateRaidWithSnapshot).Route,
            (HttpRequest http) => CreateRaidWithSnapshot(http, starting)).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> CreateRaidWithSnapshot(HttpRequest http, RaidStarting starting)
    {
        var multipart = await MultipartSnapshotReader.Read<CreateRaidWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Raid.TransactionMissingPayload")), null, ProtocolDeliverySemantics.OnlineImmediate)
            : starting.CreateWithSnapshot(multipart.Request, multipart.Payload,
                http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static IResult CreateRaid() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.SnapshotEndpointRequired")),
        null, ProtocolDeliverySemantics.ServerNotification));
}
