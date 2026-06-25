using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSnapshotOpenPayload
{
    public RemoteSnapshotOpenPayload(
        string snapshotId,
        ModSnapshotPackageMetadataDto package,
        byte[] payload)
    {
        SnapshotId = snapshotId;
        Package = package;
        Payload = payload;
    }

    public string SnapshotId { get; }

    public ModSnapshotPackageMetadataDto Package { get; }

    public byte[] Payload { get; }
}
