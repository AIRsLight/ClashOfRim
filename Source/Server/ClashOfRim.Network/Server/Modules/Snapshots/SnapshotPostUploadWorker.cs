using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotPostUploadWorker(SnapshotPostUploadJobRegistry jobs, Func<ILogger> logger)
{
    public const int MaximumAutomaticAttempts = 8;

    internal void ReconcileArtifacts(IReadOnlyCollection<SnapshotPostUploadWork> operations)
    {
        foreach (var operation in operations.Where(operation => operation.ReconcileArtifacts is not null))
        {
            try { operation.ReconcileArtifacts!(); }
            catch (Exception ex)
            {
                logger().LogWarning(ex, "Snapshot post-upload artifact reconciliation failed: processor={ProcessorId}", operation.Processor.Id);
            }
        }
    }

    internal int ProcessReady(
        IReadOnlyCollection<SnapshotPostUploadWork> operations,
        DateTimeOffset nowUtc)
    {
        SnapshotPostUploadRunner.ValidateRegistrations(operations.Select(operation => operation.Processor).ToArray());
        var deferredById = operations.Where(operation => operation.Bind is not null
                && operation.Processor.ExecutionMode == SnapshotPostUploadExecutionMode.Deferred)
            .ToDictionary(operation => operation.Processor.Id, StringComparer.Ordinal);
        foreach (SnapshotPostUploadJobRecord job in jobs.ListPrepared(nowUtc))
        {
            if (!deferredById.TryGetValue(job.ProcessorId, out SnapshotPostUploadWork? deferred)
                || deferred.RecoverPrepared is null
                || !deferred.Processor.Supports(job.Kind))
            {
                MarkFailed(
                    job,
                    $"Prepared snapshot post-upload processor '{job.ProcessorId}' is not currently recoverable.",
                    nowUtc);
                continue;
            }

            try
            {
                deferred.RecoverPrepared(job, nowUtc);
            }
            catch (Exception ex)
            {
                logger().LogWarning(
                    ex,
                    "Prepared snapshot post-upload job recovery failed: processor={ProcessorId} kind={Kind} snapshot={SnapshotId} attempt={Attempt}",
                    job.ProcessorId,
                    job.Kind,
                    job.SnapshotId,
                    job.AttemptCount + 1);
                MarkFailed(job, ex.Message, nowUtc);
            }
        }

        int completed = 0;
        foreach (SnapshotPostUploadJobRecord job in jobs.ListReady(nowUtc))
        {
            if (!deferredById.TryGetValue(job.ProcessorId, out SnapshotPostUploadWork? processor)
                || !processor.Processor.Supports(job.Kind))
            {
                MarkFailed(
                    job,
                    $"Snapshot post-upload processor '{job.ProcessorId}' is not currently available.",
                    nowUtc);
                continue;
            }

            try
            {
                var execution = processor.Bind!(job);
                execution.Process();
                jobs.MarkCompleted(job.JobId);
                if (execution.Complete is { } complete)
                {
                    try
                    {
                        complete();
                    }
                    catch (Exception ex)
                    {
                        logger().LogWarning(
                            ex,
                            "Deferred snapshot post-upload completion cleanup failed after job removal: processor={ProcessorId} job={JobId}",
                            job.ProcessorId,
                            job.JobId);
                    }
                }
                completed++;
            }
            catch (SnapshotPostUploadManualReviewException ex)
            {
                logger().LogError(
                    ex,
                    "Deferred snapshot post-upload job requires manual review: processor={ProcessorId} kind={Kind} snapshot={SnapshotId}",
                    job.ProcessorId,
                    job.Kind,
                    job.SnapshotId);
                jobs.MarkManualReview(job.JobId, ex.Message);
            }
            catch (Exception ex)
            {
                logger().LogWarning(
                    ex,
                    "Deferred snapshot post-upload processor failed: processor={ProcessorId} kind={Kind} snapshot={SnapshotId} attempt={Attempt}",
                    job.ProcessorId,
                    job.Kind,
                    job.SnapshotId,
                    job.AttemptCount + 1);
                MarkFailed(job, ex.Message, nowUtc);
            }
        }

        return completed;
    }

    private void MarkFailed(
        SnapshotPostUploadJobRecord job,
        string error,
        DateTimeOffset nowUtc)
    {
        if (job.AttemptCount + 1 >= MaximumAutomaticAttempts)
        {
            logger().LogError(
                "Snapshot post-upload job exhausted automatic retries and requires manual review: processor={ProcessorId} kind={Kind} snapshot={SnapshotId} attempts={Attempts}",
                job.ProcessorId,
                job.Kind,
                job.SnapshotId,
                MaximumAutomaticAttempts);
            jobs.MarkManualReview(job.JobId, error);
            return;
        }

        int exponent = Math.Min(job.AttemptCount, 8);
        TimeSpan retryDelay = TimeSpan.FromSeconds(Math.Min(300, 1 << exponent));
        jobs.MarkFailed(job.JobId, error, nowUtc.Add(retryDelay));
    }
}
