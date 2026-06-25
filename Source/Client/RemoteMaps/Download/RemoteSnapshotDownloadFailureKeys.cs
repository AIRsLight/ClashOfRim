namespace AIRsLight.ClashOfRim.RemoteMaps;

public readonly struct RemoteSnapshotDownloadFailureKeys
{
    public RemoteSnapshotDownloadFailureKeys(
        string metadataFailed,
        string missingSnapshot,
        string payloadFailed)
    {
        MetadataFailed = metadataFailed;
        MissingSnapshot = missingSnapshot;
        PayloadFailed = payloadFailed;
    }

    public string MetadataFailed { get; }

    public string MissingSnapshot { get; }

    public string PayloadFailed { get; }
}
