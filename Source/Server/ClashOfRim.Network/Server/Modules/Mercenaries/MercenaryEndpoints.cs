using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

internal static class MercenaryEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, MercenaryService mercenaries, MercenaryHiring hiring)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenary).Route,
            (QuoteMercenaryRequest request) => Results.Ok(mercenaries.QuoteMercenary(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenaryGuard).Route,
            (QuoteMercenaryGuardRequest request) => Results.Ok(mercenaries.QuoteMercenaryGuard(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ReportMercenaryIncident).Route,
            (ReportMercenaryIncidentRequest request) => Results.Ok(mercenaries.ReportMercenaryIncident(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryWithSnapshot).Route,
            (HttpRequest http) => HireMercenaryWithSnapshot(http, hiring)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryGuardWithSnapshot).Route,
            (HttpRequest http) => HireMercenaryGuardWithSnapshot(http, hiring)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenary).Route,
            HireMercenary).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> HireMercenaryWithSnapshot(HttpRequest http, MercenaryHiring hiring)
    {
        var multipart = await MultipartSnapshotReader.Read<HireMercenaryWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Mercenary.TransactionMissingPayload")), null, null)
            : hiring.HireMercenaryWithSnapshot(multipart.Request, multipart.Payload,
                http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static async Task<IResult> HireMercenaryGuardWithSnapshot(HttpRequest http, MercenaryHiring hiring)
    {
        var multipart = await MultipartSnapshotReader.Read<HireMercenaryGuardWithSnapshotRequest>(http);
        return Results.Ok(multipart?.Request is null
            ? new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Mercenary.GuardTransactionMissingPayload")), null, null)
            : hiring.HireMercenaryGuardWithSnapshot(multipart.Request, multipart.Payload,
                http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    // Retired JSON hire cannot trigger recovery or mutate domain state.
    internal static IResult HireMercenary() => Results.Ok(new MercenaryHireResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Economy.SnapshotEndpointRequired")), null, null));
}
