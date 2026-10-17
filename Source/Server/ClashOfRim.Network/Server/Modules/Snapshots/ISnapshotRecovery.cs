namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal interface ISnapshotRecovery
{
    // These operations run under the shared mutation gate, in this order.
    void RecoverPrerequisites();
    ISnapshotRecoveryPreparation CapturePreparation();
    void Replay();
}

internal interface ISnapshotRecoveryPreparation
{
    bool NeedsPreparation { get; }
    bool IsCurrent { get; }
    // Only Prepare runs outside the mutation gate.
    void Prepare();
    bool TryInstall();
}
