namespace AIRsLight.ClashOfRim.ClientSnapshots;

public sealed class ModSnapshotUploadResult
{
    private ModSnapshotUploadResult(
        bool success,
        string? acceptedSnapshotId,
        string? sourcePath,
        string? errorCode,
        string? message)
    {
        Success = success;
        AcceptedSnapshotId = acceptedSnapshotId;
        SourcePath = sourcePath;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Success { get; }

    public string? AcceptedSnapshotId { get; }

    public string? SourcePath { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }

    public static ModSnapshotUploadResult Ok(string acceptedSnapshotId, string sourcePath)
    {
        return new ModSnapshotUploadResult(true, acceptedSnapshotId, sourcePath, null, null);
    }

    public static ModSnapshotUploadResult Failed(string errorCode, string message)
    {
        return new ModSnapshotUploadResult(false, null, null, errorCode, message);
    }
}
