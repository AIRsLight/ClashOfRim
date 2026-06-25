using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSessionMapOpenRequest
{
    public ModWorldMapMarkerDto Target { get; set; } = new();

    public string Mode { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string SnapshotId { get; set; } = string.Empty;

    public string RelatedEventId { get; set; } = string.Empty;

    public ModSnapshotPackageMetadataDto? Package { get; set; }

    public byte[] Payload { get; set; } = System.Array.Empty<byte>();

    public bool CloseExistingObservationSessions { get; set; } = true;

    public string FailureCleanupReason { get; set; } = "remote session map generation failure";
}
