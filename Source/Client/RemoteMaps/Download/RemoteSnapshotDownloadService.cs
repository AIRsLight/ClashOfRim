using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSnapshotDownloadService
{
    private readonly ClashOfRimModNetworkClient client;

    public RemoteSnapshotDownloadService(ClashOfRimModNetworkClient client)
    {
        this.client = client;
    }

    public async Task<RemoteSnapshotDownloadResult> DownloadAsync(
        RemoteSnapshotDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> metadata =
            await client.DownloadLatestSnapshotAsync(
                request.UserId,
                request.ColonyId,
                request.AuthorizationEventId,
                request.AuthorizationScope,
                cancellationToken);

        ClashOfRimClientNetworkResult<byte[]>? payload = null;
        string? snapshotId = metadata.Response?.SnapshotId ?? metadata.Response?.Package?.SnapshotId;
        if (metadata.Success
            && metadata.Response?.Result?.Accepted == true
            && !string.IsNullOrWhiteSpace(snapshotId))
        {
            payload = await client.DownloadLatestSnapshotPayloadAsync(
                request.UserId,
                request.ColonyId,
                snapshotId!,
                request.AuthorizationEventId,
                request.AuthorizationScope,
                cancellationToken);
        }

        return new RemoteSnapshotDownloadResult(metadata, payload);
    }
}
