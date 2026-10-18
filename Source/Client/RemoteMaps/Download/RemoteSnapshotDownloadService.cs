using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSnapshotDownloadService
{
    private readonly ClientRemoteSnapshotDownload download;

    public RemoteSnapshotDownloadService(ClashOfRimModNetworkClient client)
    {
        download = new ClientRemoteSnapshotDownload(client.SnapshotDownloads);
    }

    public Task<RemoteSnapshotDownloadResult> DownloadAsync(
        RemoteSnapshotDownloadRequest request,
        CancellationToken cancellationToken = default)
        => download.DownloadAsync(new ClientSnapshotDownloadTarget(request.UserId ?? string.Empty, request.ColonyId ?? string.Empty,
            request.AuthorizationEventId, request.AuthorizationScope), cancellationToken);
}
