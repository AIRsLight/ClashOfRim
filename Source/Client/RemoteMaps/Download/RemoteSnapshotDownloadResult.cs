using AIRsLight.ClashOfRim.ClientNetwork;

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

    public string? SnapshotId => Metadata.Response?.SnapshotId ?? Metadata.Response?.Package?.SnapshotId;
}
