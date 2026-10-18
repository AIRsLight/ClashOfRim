using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed record SnapshotPostUploadInput(SnapshotPostUploadKind Kind, LatestSnapshotRecord Snapshot,
    string UserId, string ColonyId, string? SessionId, DateTimeOffset OccurredAtUtc,
    SnapshotPostUploadExtraData ExtraData, bool RegisterPlayerColonySite)
{
    internal SnapshotPostUploadJobInput Job => new(Kind, UserId, ColonyId, SessionId, Snapshot?.Identity.SnapshotId, OccurredAtUtc);
}

// Null delegates represent absent plugin capabilities; the runner checks them at execution time.
internal sealed record SnapshotPostUploadOperation(ISnapshotPostUploadProcessor Processor,
    Action<SnapshotPostUploadInput>? Process = null,
    Func<SnapshotPostUploadInput, string>? CapturePayload = null,
    Action<SnapshotPostUploadInput>? Schedule = null);
