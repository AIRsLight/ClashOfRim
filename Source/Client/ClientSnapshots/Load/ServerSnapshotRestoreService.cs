using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

// Public compatibility entry point; validation and rewriting belong to the module.
public static class ServerSnapshotRestoreService
{
    public static bool TryBuildServerSessionSaveBytes(ModSnapshotPackageMetadataDto package, byte[] payload,
        string userId, string colonyId, out string saveName, out byte[] saveBytes, out string failureReason)
    {
        bool success = ClientSnapshotRestoreSave.TryBuild(package, payload, userId, colonyId, out saveName, out saveBytes, out var failure);
        failureReason = failure is null ? string.Empty : ClientSnapshotRestorePresentation.Format(failure);
        return success;
    }
}
