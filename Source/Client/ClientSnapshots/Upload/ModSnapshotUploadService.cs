using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

// Public compatibility facade. Capture, upload, admission and publication belong to
// ClientSnapshotUploader; preserve these signatures for external callers.
public sealed class ModSnapshotUploadService
{
    private readonly ClientSnapshotUploader uploader;
    private readonly ClientSynchronizationCoordinator.Lease? continuation;

    public ModSnapshotUploadService(ClashOfRimSettings settings) : this(settings, null) { }

    internal ModSnapshotUploadService(ClashOfRimSettings settings, ClientSynchronizationCoordinator.Lease? continuation)
    {
        uploader = ClientSnapshotComposition.Create(settings ?? throw new ArgumentNullException(nameof(settings)));
        this.continuation = continuation;
    }

    public Task<ModSnapshotUploadResult> UploadConfiguredSnapshotAsync(CancellationToken cancellationToken = default) =>
        UploadConfiguredSnapshotAsync(false, cancellationToken: cancellationToken);

    public async Task<ModSnapshotUploadResult> UploadConfiguredSnapshotAsync(
        bool removeRaidBattleSessions = false, string? confirmationOperation = null, string? snapshotUploadKind = null,
        IReadOnlyList<ModSnapshotAchievementCandidateDto>? achievementCandidates = null, CancellationToken cancellationToken = default) =>
        ClientSnapshotUploadPresentation.ToPublicResult(await uploader.UploadAsync(removeRaidBattleSessions,
            confirmationOperation, snapshotUploadKind, achievementCandidates, continuation, cancellationToken));
}
