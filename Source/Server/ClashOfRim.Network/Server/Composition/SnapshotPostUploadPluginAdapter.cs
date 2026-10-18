using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

// The public plugin contract retains State; only argument conversion and plugin invocation live here.
internal static class SnapshotPostUploadPluginAdapter
{
    internal static SnapshotPostUploadInput Input(SnapshotPostUploadContext context) => new(context.Kind, context.Snapshot,
        context.UserId, context.ColonyId, context.SessionId, context.OccurredAtUtc, context.ExtraData, context.RegisterPlayerColonySite);

    internal static SnapshotPostUploadOperation Bind(ISnapshotPostUploadProcessor processor, SnapshotPostUploadContext context) => new(processor,
        processor is IInlineSnapshotPostUploadProcessor inline ? _ => inline.Process(context) : null,
        processor is IDeferredSnapshotPostUploadProcessor deferred ? _ => deferred.CapturePayload(context) : null,
        processor is IScheduledDeferredSnapshotPostUploadProcessor scheduled ? _ => scheduled.Schedule(context) : null);
}
