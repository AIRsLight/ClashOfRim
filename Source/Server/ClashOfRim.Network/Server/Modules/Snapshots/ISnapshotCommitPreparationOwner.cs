using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Both operations run under the same snapshot mutation gate; preparation never accesses pending state.
internal interface ISnapshotCommitPreparationOwner
{
    bool IsCurrent(IReadOnlyList<SnapshotCommitIntent> captured);
    void InstallPreparedPayload(SnapshotCommitIntent intent, PreparedSnapshotPayload payload);
}
