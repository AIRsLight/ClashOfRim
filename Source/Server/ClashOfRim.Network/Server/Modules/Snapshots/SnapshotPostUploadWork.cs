using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed record SnapshotPostUploadWork(ISnapshotPostUploadProcessor Processor,
    Func<SnapshotPostUploadJobRecord, SnapshotPostUploadExecution>? Bind = null,
    Action<SnapshotPostUploadJobRecord, DateTimeOffset>? RecoverPrepared = null,
    Action? ReconcileArtifacts = null);

internal sealed record SnapshotPostUploadExecution(Action Process, Action? Complete = null);
