namespace AIRsLight.ClashOfRim.Save;

public sealed record SaveSnapshotEnvelope(
    string PackageVersion,
    SnapshotIdentity Identity,
    DateTimeOffset CreatedAtUtc,
    string SourceFileName,
    string? RimWorldVersion,
    SnapshotPayloadEncoding PayloadEncoding,
    long OriginalSaveBytes,
    long PayloadBytes,
    string OriginalSha256,
    string PayloadSha256,
    string? PreviousSnapshotId = null,
    string? LineageToken = null,
    string? NextLineageToken = null,
    long? GameTicks = null);
