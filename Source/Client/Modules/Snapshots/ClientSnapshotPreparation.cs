using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Capture must run on the owning game's thread; packaging can run after that capture.
// Domain confirmations retain pending achievements. Only ordinary uploads submit and remove them.
internal sealed class ClientSnapshotPreparation
{
    private readonly IClientSnapshotGame game;
    internal ClientSnapshotPreparation(IClientSnapshotGame game) => this.game = game;

    internal bool TryBuild(string ownerId, string colonyId, out ModSnapshotPackageBuildResult build, out SnapshotUploadFailure? failure)
        => TryBuild(game.BindCurrentGame(), ownerId, colonyId, out build, out failure);

    internal static bool TryBuild(IClientSnapshotGameSession session, string ownerId, string colonyId,
        out ModSnapshotPackageBuildResult build, out SnapshotUploadFailure? failure)
    {
        SnapshotSaveCapture capture = Capture(session, false);
        build = Build(capture, ownerId, colonyId);
        bool success = build.Success && build.Package is not null && build.Payload is not null;
        failure = success ? null : capture.Failure ?? SnapshotUploadFailure.FromPackageBuild(build);
        return success;
    }

    internal static SnapshotSaveCapture Capture(IClientSnapshotGameSession session, bool removeRaidBattleSessions)
    {
        try { return session.Capture(removeRaidBattleSessions); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(null, null, Array.Empty<ModSnapshotAchievementCandidateDto>(),
                new("SaveToMemoryException", $"{ex.GetType().Name} {ex.Message}", SnapshotUploadMessage.SaveFailed));
        }
    }

    internal static ModSnapshotPackageBuildResult Build(SnapshotSaveCapture capture, string ownerId, string colonyId,
        string? uploadKind = null, IReadOnlyList<ModSnapshotAchievementCandidateDto>? submittedAchievements = null)
    {
        if (capture.Failure is { } failure)
            return ModSnapshotPackageBuildResult.Failed(failure.Code, failure.Message ?? failure.Code);
        byte[] saveBytes = capture.SaveBytes ?? Array.Empty<byte>();
        // Completion only receives captured bytes/values. It must never read live
        // game state; ordinary uploads execute this with background packaging.
        try { if (capture.CompleteSave is { } complete) saveBytes = complete(saveBytes); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ModSnapshotPackageBuildResult.Failed("SaveCompletionFailed", $"{ex.GetType().Name} {ex.Message}"); }
        // An invalid XML payload remains intact; the builder returns the structured parse failure.
        if (submittedAchievements is { Count: > 0 }) saveBytes = ClientSnapshotSaveData.RemovePendingAchievementQueue(saveBytes, out _);
        var build = ClientSnapshotPackageBuilder.FromSaveBytes(saveBytes, "memory", ownerId, colonyId, DateTime.UtcNow);
        if (build.Success && build.Package is not null)
        {
            build.Package.SnapshotUploadKind = uploadKind;
            build.Package.DefenderThreatPoints = capture.DefenderThreatPoints;
        }
        return build;
    }
}
