namespace AIRsLight.ClashOfRim.Save;

public sealed record SnapshotUploadResult(
    SnapshotUploadResultKind Kind,
    string Message,
    LatestSnapshotRecord? AcceptedSnapshot = null,
    string? SnapshotUploadKind = null)
{
    public bool Accepted => Kind == SnapshotUploadResultKind.Accepted;

    public static SnapshotUploadResult Reject(SnapshotUploadResultKind kind, string message)
    {
        if (kind == SnapshotUploadResultKind.Accepted)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Accepted results must include a snapshot record.");
        }

        return new SnapshotUploadResult(kind, message);
    }

    public static SnapshotUploadResult Accept(LatestSnapshotRecord snapshot, string? snapshotUploadKind = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SnapshotUploadResult(
            SnapshotUploadResultKind.Accepted,
            "Snapshot upload passed validation.",
            snapshot,
            snapshotUploadKind);
    }
}
