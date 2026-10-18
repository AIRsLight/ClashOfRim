using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, WorldSessionPreparation preparation, WorldConfigurationRead read,
        WorldConfigurationSubmission submission, WorldFeatureNames featureNames, WorldSubstrateTransfer substrate, WorldSynchronization synchronization,
        ColonySiteRegistration registration, ColonyRelocation relocation)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PreflightColonyRelocation).Route,
            (PreflightColonyRelocationRequest request) => Results.Ok(relocation.Preflight(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.ConfirmColonyRelocation).Route,
            (ConfirmColonyRelocationRequest request) => Results.Ok(relocation.Confirm(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RegisterPlayerColonySites).Route,
            (RegisterPlayerColonySitesRequest request) => Results.Ok(registration.Register(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route,
            (SyncWorldMapMarkersRequest request) => Results.Ok(synchronization.Read(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route,
            (SyncRuntimeWorldObjectsRequest request) => Results.Ok(synchronization.Replace(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.PrepareWorldSession).Route,
            (PrepareWorldSessionRequest request) => Results.Ok(preparation.Prepare(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetWorldConfiguration).Route,
            (GetWorldConfigurationRequest request) => Results.Ok(read.Get(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldConfiguration).Route,
            (SubmitWorldConfigurationRequest request) => Results.Ok(submission.Submit(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.SubmitWorldFeatureNames).Route,
            (SubmitWorldFeatureNamesRequest request) => Results.Ok(featureNames.Submit(request)));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadWorldSubstrate).Route,
            (HttpRequest http) => Upload(http, substrate));
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadWorldSubstrate).Route,
            (DownloadWorldSubstrateRequest request) => Download(request, substrate));
    }

    internal static async Task<IResult> Upload(HttpRequest http, WorldSubstrateTransfer substrate)
    {
        var multipart = await MultipartSnapshotReader.Read<UploadWorldSubstrateRequest>(http);
        return Results.Ok(substrate.Upload(multipart?.Request, multipart?.Payload, http.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static IResult Download(DownloadWorldSubstrateRequest request, WorldSubstrateTransfer substrate)
    {
        var result = substrate.Download(request);
        return result.Status switch
        {
            WorldSubstrateDownloadStatus.Available => Results.File(result.Payload!, "application/octet-stream", "world-substrate.cors.gz"),
            WorldSubstrateDownloadStatus.InvalidRequest => Results.BadRequest(),
            WorldSubstrateDownloadStatus.Unauthorized => Results.Unauthorized(),
            WorldSubstrateDownloadStatus.NotFound => Results.NotFound(),
            _ => throw new InvalidOperationException("Unknown world substrate download outcome.")
        };
    }
}
