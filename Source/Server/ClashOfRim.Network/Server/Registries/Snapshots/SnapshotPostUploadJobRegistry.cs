using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

public sealed class SnapshotPostUploadJobRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, SnapshotPostUploadJobRecord> jobsById = new(StringComparer.Ordinal);
    private readonly ISnapshotPostUploadJobStore? persistence;

    public SnapshotPostUploadJobRegistry()
        : this(null)
    {
    }

    internal SnapshotPostUploadJobRegistry(ISnapshotPostUploadJobStore? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public SnapshotPostUploadJobRecord Enqueue(
        string processorId,
        SnapshotPostUploadContext context,
        string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payloadJson);

        string snapshotId = context.Snapshot?.Identity.SnapshotId
            ?? $"{context.UserId}:{context.ColonyId}:{context.OccurredAtUtc.UtcTicks}";
        string jobId = snapshotId + ":" + processorId;
        lock (gate)
        {
            if (jobsById.TryGetValue(jobId, out SnapshotPostUploadJobRecord? existing))
            {
                return existing;
            }

            var record = new SnapshotPostUploadJobRecord(
                jobId,
                processorId,
                context.Kind,
                context.UserId,
                context.ColonyId,
                context.SessionId,
                snapshotId,
                context.OccurredAtUtc,
                payloadJson,
                AttemptCount: 0,
                NextAttemptAtUtc: context.OccurredAtUtc,
                LastError: null);
            persistence?.Upsert(record);
            jobsById[jobId] = record;
            return record;
        }
    }

    public IReadOnlyList<SnapshotPostUploadJobRecord> ListReady(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            return jobsById.Values
                .Where(job => job.NextAttemptAtUtc <= nowUtc)
                .OrderBy(job => job.NextAttemptAtUtc)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public void MarkCompleted(string jobId)
    {
        lock (gate)
        {
            if (jobsById.ContainsKey(jobId))
            {
                persistence?.Delete(jobId);
                jobsById.Remove(jobId);
            }
        }
    }

    public void MarkFailed(string jobId, string error, DateTimeOffset nextAttemptAtUtc)
    {
        lock (gate)
        {
            if (!jobsById.TryGetValue(jobId, out SnapshotPostUploadJobRecord? current))
            {
                return;
            }

            SnapshotPostUploadJobRecord updated = current with
            {
                AttemptCount = current.AttemptCount + 1,
                NextAttemptAtUtc = nextAttemptAtUtc,
                LastError = error
            };
            persistence?.Upsert(updated);
            jobsById[jobId] = updated;
        }
    }

    private void Load()
    {
        if (persistence is null)
        {
            return;
        }

        foreach (SnapshotPostUploadJobRecord record in persistence.ReadAll())
        {
            if (!string.IsNullOrWhiteSpace(record.JobId))
            {
                jobsById[record.JobId] = record;
            }
        }
    }
}

public sealed record SnapshotPostUploadJobRecord(
    string JobId,
    string ProcessorId,
    SnapshotPostUploadKind Kind,
    string UserId,
    string ColonyId,
    string? SessionId,
    string SnapshotId,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastError);
