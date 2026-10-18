using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal sealed class ClientRemoteSnapshotDownload
{
    private readonly ClientSnapshotDownloadProtocol protocol;
    internal ClientRemoteSnapshotDownload(ClientSnapshotDownloadProtocol protocol) => this.protocol = protocol;

    internal async Task<RemoteSnapshotDownloadResult> DownloadAsync(ClientSnapshotDownloadTarget target,
        CancellationToken token = default)
    {
        var metadata = await protocol.MetadataAsync(target, token).ConfigureAwait(false);
        ClashOfRimClientNetworkResult<byte[]>? payload = null;
        string? snapshotId = SnapshotId(metadata.Response);
        if (metadata.Success && metadata.Response?.Result?.Accepted == true && !string.IsNullOrWhiteSpace(snapshotId))
            payload = await protocol.PayloadAsync(target, snapshotId!, token).ConfigureAwait(false);
        return new RemoteSnapshotDownloadResult(metadata, payload);
    }

    internal static string? SnapshotId(ModDownloadLatestSnapshotResponseDto? metadata) =>
        metadata?.SnapshotId ?? metadata?.Package?.SnapshotId;

    internal static bool TryGetOpenPayload(RemoteSnapshotDownloadResult download,
        out RemoteSnapshotOpenPayload? openPayload, out ClientRemoteSnapshotFailure? failure)
    {
        openPayload = null;
        var metadata = download.Metadata;
        if (!metadata.Success || metadata.Response?.Result?.Accepted != true)
            failure = new(ClientRemoteSnapshotFailureKind.Metadata, metadata.ErrorCode,
                metadata.Response?.Result?.Message ?? metadata.Message);
        else if (metadata.Response.Package is not { } package || string.IsNullOrWhiteSpace(download.SnapshotId))
            failure = new(ClientRemoteSnapshotFailureKind.MissingSnapshot);
        else if (download.Payload is not { Success: true, Response: not null } payload)
            failure = new(ClientRemoteSnapshotFailureKind.Payload, download.Payload?.ErrorCode ?? "MissingPayload",
                download.Payload?.Message);
        else
        {
            openPayload = new RemoteSnapshotOpenPayload(download.SnapshotId!, package, payload.Response);
            failure = null;
            return true;
        }
        return false;
    }
}

internal enum ClientRemoteSnapshotFailureKind { Metadata, MissingSnapshot, Payload }
internal sealed record ClientRemoteSnapshotFailure(ClientRemoteSnapshotFailureKind Kind, string? Code = null, string? Message = null);
