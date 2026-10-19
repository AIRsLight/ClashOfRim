using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal static class SnapshotEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, SnapshotUploads uploads, SnapshotDownloads downloads)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.UploadSnapshot).Route,
            (HttpRequest request) => UploadSnapshot(request, uploads)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route,
            (DownloadLatestSnapshotRequest request) => DownloadLatestSnapshot(request, downloads))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route,
            (DownloadLatestSnapshotPayloadRequest request) => DownloadLatestSnapshotPayload(request, downloads))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> UploadSnapshot(HttpRequest httpRequest, SnapshotUploads uploads)
    {
        var multipart = await MultipartSnapshotReader.Read<UploadSnapshotMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
            return Results.Ok(new UploadSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Snapshot.UploadMissingPayload")),
                acceptedSnapshotId: null, uploadResult: "MissingMultipartPayload"));

        return Results.Ok(uploads.Upload(multipart.Request, multipart.Payload));
    }

    internal static IResult DownloadLatestSnapshot(DownloadLatestSnapshotRequest request, SnapshotDownloads downloads)
        => Results.Ok(downloads.Metadata(request));

    internal static IResult DownloadLatestSnapshotPayload(DownloadLatestSnapshotPayloadRequest request, SnapshotDownloads downloads)
    {
        var download = downloads.Payload(request);
        return download.Status switch
        {
            SnapshotDownloadStatus.Available => Results.File(download.Package!.Payload, "application/octet-stream",
                (download.Package.Envelope.Identity.SnapshotId ?? "latest") + ".payload"),
            SnapshotDownloadStatus.InvalidRequest => Results.BadRequest(new { result = download.Result }),
            SnapshotDownloadStatus.Unauthorized => Results.Json(new { result = download.Result }, statusCode: StatusCodes.Status401Unauthorized),
            SnapshotDownloadStatus.UnsupportedStore => Results.Json(new { result = download.Result }, statusCode: StatusCodes.Status500InternalServerError),
            SnapshotDownloadStatus.NotFound => Results.Json(new { result = download.Result }, statusCode: StatusCodes.Status404NotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(download.Status))
        };
    }
}
