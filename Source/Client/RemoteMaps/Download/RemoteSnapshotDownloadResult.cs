using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSnapshotDownloadResult
{
    public RemoteSnapshotDownloadResult(
        ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> metadata,
        ClashOfRimClientNetworkResult<byte[]>? payload)
    {
        Metadata = metadata;
        Payload = payload;
    }

    public ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto> Metadata { get; }

    public ClashOfRimClientNetworkResult<byte[]>? Payload { get; }

    public string? SnapshotId => ClientRemoteSnapshotDownload.SnapshotId(Metadata.Response);

    internal ClientRemoteSnapshotDocument? PreparedDocument { get; set; }
}
