using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public sealed class ModSnapshotPackageBuildResult
{
    private ModSnapshotPackageBuildResult(
        bool success,
        ModSnapshotPackageMetadataDto? package,
        byte[]? payload,
        byte[]? originalPayload,
        string? sourcePath,
        string? errorCode,
        string? message)
    {
        Success = success;
        Package = package;
        Payload = payload;
        OriginalPayload = originalPayload;
        SourcePath = sourcePath;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Success { get; }

    public ModSnapshotPackageMetadataDto? Package { get; }

    public byte[]? Payload { get; }

    public byte[]? OriginalPayload { get; }

    public string? SourcePath { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }

    public static ModSnapshotPackageBuildResult Ok(ModSnapshotPackageMetadataDto package, byte[] payload, byte[] originalPayload, string sourcePath)
    {
        return new ModSnapshotPackageBuildResult(true, package, payload, originalPayload, sourcePath, null, null);
    }

    public static ModSnapshotPackageBuildResult Failed(string errorCode, string message)
    {
        return new ModSnapshotPackageBuildResult(false, null, null, null, null, errorCode, message);
    }
}
