using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Persisted job identity without a server host or plugin execution context.
internal sealed record SnapshotPostUploadJobInput(SnapshotPostUploadKind Kind, string UserId, string ColonyId,
    string? SessionId, string? SnapshotId, DateTimeOffset OccurredAtUtc)
{
    internal string ResolvedSnapshotId => SnapshotId ?? $"{UserId}:{ColonyId}:{OccurredAtUtc.UtcTicks}";
}
