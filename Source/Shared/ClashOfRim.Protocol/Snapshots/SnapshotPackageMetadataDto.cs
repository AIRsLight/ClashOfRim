namespace AIRsLight.ClashOfRim.Protocol;

public sealed class SnapshotPackageMetadataDto
{
    public SnapshotPackageMetadataDto(
        string packageVersion,
        string ownerId,
        string colonyId,
        string snapshotId,
        string? rimWorldVersion,
        string payloadEncoding,
        long originalSaveBytes,
        long payloadBytes,
        string originalSha256,
        string payloadSha256,
        string? previousSnapshotId = null,
        string? lineageToken = null,
        string? nextLineageToken = null,
        long? gameTicks = null,
        string? snapshotUploadKind = null,
        float? defenderThreatPoints = null)
    {
        PackageVersion = packageVersion;
        OwnerId = ownerId;
        ColonyId = colonyId;
        SnapshotId = snapshotId;
        RimWorldVersion = rimWorldVersion;
        PayloadEncoding = payloadEncoding;
        OriginalSaveBytes = originalSaveBytes;
        PayloadBytes = payloadBytes;
        OriginalSha256 = originalSha256;
        PayloadSha256 = payloadSha256;
        PreviousSnapshotId = previousSnapshotId;
        LineageToken = lineageToken;
        NextLineageToken = nextLineageToken;
        GameTicks = gameTicks;
        SnapshotUploadKind = snapshotUploadKind;
        DefenderThreatPoints = defenderThreatPoints;
    }

    public string PackageVersion { get; }

    public string OwnerId { get; }

    public string ColonyId { get; }

    public string SnapshotId { get; }

    public string? RimWorldVersion { get; }

    public string PayloadEncoding { get; }

    public long OriginalSaveBytes { get; }

    public long PayloadBytes { get; }

    public string OriginalSha256 { get; }

    public string PayloadSha256 { get; }

    public string? PreviousSnapshotId { get; }

    public string? LineageToken { get; }

    public string? NextLineageToken { get; }

    public long? GameTicks { get; }

    public string? SnapshotUploadKind { get; }

    public float? DefenderThreatPoints { get; }
}
