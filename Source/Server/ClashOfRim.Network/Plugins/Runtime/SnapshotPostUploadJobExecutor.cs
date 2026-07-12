using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public static class SnapshotPostUploadJobExecutor
{
    public static int ProcessReady(
        ClashOfRimNetworkState state,
        IReadOnlyCollection<ISnapshotPostUploadProcessor> processors,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(processors);
        SnapshotPostUploadPipeline.ValidateRegistrations(processors);

        Dictionary<string, IDeferredSnapshotPostUploadProcessor> deferredById = processors
            .OfType<IDeferredSnapshotPostUploadProcessor>()
            .Where(processor => processor.ExecutionMode == SnapshotPostUploadExecutionMode.Deferred)
            .GroupBy(processor => processor.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        int completed = 0;
        foreach (SnapshotPostUploadJobRecord job in state.SnapshotPostUploadJobs.ListReady(nowUtc))
        {
            if (!deferredById.TryGetValue(job.ProcessorId, out IDeferredSnapshotPostUploadProcessor? processor)
                || !processor.Supports(job.Kind))
            {
                MarkFailed(
                    state,
                    job,
                    $"Snapshot post-upload processor '{job.ProcessorId}' is not currently available.",
                    nowUtc);
                continue;
            }

            try
            {
                processor.ProcessDeferred(new SnapshotPostUploadDeferredContext(
                    state,
                    job.JobId,
                    job.ProcessorId,
                    job.Kind,
                    job.UserId,
                    job.ColonyId,
                    job.SessionId,
                    job.SnapshotId,
                    job.OccurredAtUtc,
                    job.PayloadJson,
                    job.AttemptCount));
                state.SnapshotPostUploadJobs.MarkCompleted(job.JobId);
                completed++;
            }
            catch (Exception ex)
            {
                state.RuntimeLogger.LogWarning(
                    ex,
                    "Deferred snapshot post-upload processor failed: processor={ProcessorId} kind={Kind} snapshot={SnapshotId} attempt={Attempt}",
                    job.ProcessorId,
                    job.Kind,
                    job.SnapshotId,
                    job.AttemptCount + 1);
                MarkFailed(state, job, ex.Message, nowUtc);
            }
        }

        return completed;
    }

    private static void MarkFailed(
        ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job,
        string error,
        DateTimeOffset nowUtc)
    {
        int exponent = Math.Min(job.AttemptCount, 8);
        TimeSpan retryDelay = TimeSpan.FromSeconds(Math.Min(300, 1 << exponent));
        state.SnapshotPostUploadJobs.MarkFailed(job.JobId, error, nowUtc.Add(retryDelay));
    }
}

internal sealed class SnapshotPostUploadProcessorSource
{
    private readonly Func<IReadOnlyList<ISnapshotPostUploadProcessor>> resolve;

    public SnapshotPostUploadProcessorSource(Func<IReadOnlyList<ISnapshotPostUploadProcessor>> resolve)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public IReadOnlyList<ISnapshotPostUploadProcessor> Resolve()
    {
        return resolve();
    }
}

internal sealed class SnapshotPostUploadBackgroundService : BackgroundService
{
    private readonly ClashOfRimNetworkState state;
    private readonly SnapshotPostUploadProcessorSource processors;

    public SnapshotPostUploadBackgroundService(
        ClashOfRimNetworkState state,
        SnapshotPostUploadProcessorSource processors)
    {
        this.state = state;
        this.processors = processors;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int completed = SnapshotPostUploadJobExecutor.ProcessReady(
                    state,
                    processors.Resolve(),
                    DateTimeOffset.UtcNow);
                TimeSpan delay = completed > 0 ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }
}
