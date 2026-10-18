using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public static class SnapshotPostUploadJobExecutor
{
    public const int MaximumAutomaticAttempts = SnapshotPostUploadWorker.MaximumAutomaticAttempts;

    public static int ProcessReady(ClashOfRimNetworkState state,
        IReadOnlyCollection<ISnapshotPostUploadProcessor> processors, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(processors);
        return new SnapshotPostUploadWorker(state.SnapshotPostUploadJobs, () => state.RuntimeLogger)
            .ProcessReady(new SnapshotPostUploadPluginAdapter(state).BindWork(processors).ToArray(), nowUtc);
    }
}
