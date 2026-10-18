using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

// The public plugin contract retains State; only argument conversion and plugin invocation live here.
internal sealed class SnapshotPostUploadPluginAdapter(ClashOfRimNetworkState state) : ISnapshotPostUploadPlugins
{
    public IEnumerable<SnapshotPostUploadOperation> Bind(SnapshotPostUploadInput input, IEnumerable<ISnapshotPostUploadProcessor> processors)
    {
        var context = new SnapshotPostUploadContext(state, input.Kind, input.Snapshot, input.UserId, input.ColonyId,
            input.SessionId, input.OccurredAtUtc, input.ExtraData, input.RegisterPlayerColonySite);
        return processors.Where(processor => processor is not null).Select(processor => Bind(processor, context));
    }

    internal static SnapshotPostUploadInput Input(SnapshotPostUploadContext context) => new(context.Kind, context.Snapshot,
        context.UserId, context.ColonyId, context.SessionId, context.OccurredAtUtc, context.ExtraData, context.RegisterPlayerColonySite);

    internal static SnapshotPostUploadOperation Bind(ISnapshotPostUploadProcessor processor, SnapshotPostUploadContext context) => new(processor,
        processor is IInlineSnapshotPostUploadProcessor inline ? _ => inline.Process(context) : null,
        processor is IDeferredSnapshotPostUploadProcessor deferred ? _ => deferred.CapturePayload(context) : null,
        processor is IScheduledDeferredSnapshotPostUploadProcessor scheduled ? _ => scheduled.Schedule(context) : null);
}
