using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotPostUploadBackground(SnapshotPostUploadWorker worker,
    Func<IReadOnlyList<SnapshotPostUploadWork>> resolve, SnapshotMutationCoordinator mutations,
    ColonyDeletionService deletions, WorldConfigurationRegistry world, SnapshotCommitService commits,
    IAuthoritativeEventLedger ledger, RaidTimeouts raids, Func<ILogger> logger)
{
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            worker.ReconcileArtifacts(resolve());
            DateTimeOffset nextTimeoutRecovery = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextTimeoutRecovery)
                {
                    nextTimeoutRecovery = DateTimeOffset.UtcNow.AddSeconds(5);
                    RecoverPending();
                }
                int completed = worker.ProcessReady(resolve(), DateTimeOffset.UtcNow);
                TimeSpan delay = completed > 0 ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private void RecoverPending()
    {
        try
        {
            if (deletions.HasPending || world.HasPendingLayerNotifications || commits.HasPendingUploads)
                mutations.Recover();
        }
        catch (Exception ex)
        {
            logger().LogWarning(ex, "Snapshot, world notification or colony deletion recovery failed; intent retained.");
        }
        try
        {
            if (RaidTimeoutProcessor.PendingWork(ledger).Count > 0) raids.RecoverPending(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger().LogWarning(ex, "Pending raid timeout recovery failed; retry marker retained.");
        }
    }
}
