using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal interface IClientSnapshotStore
{
    ClashOfRimClientNetworkContext ReadContext();
    void Publish(string snapshotId, string? lineageToken);
    void Persist();
}

internal interface IClientSnapshotGame
{
    IClientSnapshotGameSession BindCurrentGame();
}

// Bound once, before waiting for upload admission. A replaced Game must discard this work.
internal interface IClientSnapshotGameSession
{
    void Enqueue(Action action, Action onDiscard);
    bool HasGame { get; }
    string RuntimeSnapshotId { get; }
    SnapshotSaveCapture Capture(bool removeRaidBattleSessions);
    void Publish(string snapshotId, string? lineageToken, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements);
}

internal interface IClientSnapshotTransport
{
    Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(
        ClashOfRimClientNetworkContext context, string idempotencyKey, ModSnapshotPackageMetadataDto package,
        byte[] payload, string? confirmationOperation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements,
        CancellationToken cancellationToken);
}

internal sealed record SnapshotSaveCapture(byte[]? SaveBytes, float? DefenderThreatPoints,
    IReadOnlyList<ModSnapshotAchievementCandidateDto> Achievements, SnapshotUploadFailure? Failure = null);
internal sealed record SnapshotUploadFailure(string Code, string? Message, SnapshotUploadMessage Fallback,
    string? ExpectedSnapshot = null, string? RuntimeSnapshot = null, SnapshotPackageError? PackageError = null)
{
    internal static SnapshotUploadFailure FromPackageBuild(ModSnapshotPackageBuildResult build) =>
        new(build.ErrorCode ?? "SnapshotBuildFailed", build.Message, SnapshotUploadMessage.BuildFailed, PackageError: build.ErrorKind);
}
internal enum SnapshotUploadMessage
{
    NotConfigured, SaveFailed, BuildFailed, UploadFailed, ServerRejected, NoRunningGame, LineageMissing, LineageMismatch
}
internal sealed record ClientSnapshotUploadResult(string? AcceptedSnapshotId, string? SourcePath, SnapshotUploadFailure? Failure)
{
    internal static ClientSnapshotUploadResult Failed(string code, string? message, SnapshotUploadMessage fallback,
        string? expected = null, string? actual = null) => new(null, null, new(code, message, fallback, expected, actual));
}
