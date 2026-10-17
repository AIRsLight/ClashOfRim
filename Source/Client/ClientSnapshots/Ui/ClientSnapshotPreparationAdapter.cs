using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

// Preserve the legacy domain callers' textual result until each domain moves to structured failures.
internal sealed class ClientSnapshotPreparationAdapter
{
    private readonly ClientSnapshotPreparation preparation;
    internal ClientSnapshotPreparationAdapter(IClientSnapshotGame game) => preparation = new(game);

    internal bool TryBuild(string ownerId, string colonyId, out ModSnapshotPackageBuildResult build, out string failureReason)
    {
        bool success = preparation.TryBuild(ownerId, colonyId, out build, out var failure);
        failureReason = success ? string.Empty : ClientSnapshotPreparationPresentation.FailureReason(failure!);
        return success;
    }
}
