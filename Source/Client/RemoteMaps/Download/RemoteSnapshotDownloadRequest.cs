namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSnapshotDownloadRequest
{
    public string UserId { get; set; } = string.Empty;

    public string ColonyId { get; set; } = string.Empty;

    public string? AuthorizationEventId { get; set; }

    public string? AuthorizationScope { get; set; }
}
