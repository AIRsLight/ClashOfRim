using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public enum SnapshotPostUploadKind
{
    AuthoritativeColonySnapshot,
    RaidSettlementEvidence
}

public enum SnapshotPostUploadStage
{
    AuthoritativeProjection = 100,
    DerivedMetrics = 200,
    EventReconciliation = 300,
    Notification = 400
}

public enum SnapshotPostUploadFailureMode
{
    AbortPipeline,
    ContinuePipeline
}

public enum SnapshotPostUploadExecutionMode
{
    Inline,
    Deferred
}

public sealed record SnapshotPostUploadExtraData(
    string? SnapshotUploadKind,
    string? ConfirmationOperation,
    IReadOnlyCollection<SnapshotAchievementCandidateDto>? AchievementCandidates)
{
    public static SnapshotPostUploadExtraData Empty { get; } = new(null, null, null);

    public RaidSettlementPostUploadData? RaidSettlement { get; init; }
}

public sealed record SnapshotPostUploadContext(
    ClashOfRimNetworkState State,
    SnapshotPostUploadKind Kind,
    LatestSnapshotRecord Snapshot,
    string UserId,
    string ColonyId,
    string? SessionId,
    DateTimeOffset OccurredAtUtc,
    SnapshotPostUploadExtraData ExtraData,
    bool RegisterPlayerColonySite);

public sealed record SnapshotPostUploadDeferredContext(
    ClashOfRimNetworkState State,
    string JobId,
    string ProcessorId,
    SnapshotPostUploadKind Kind,
    string UserId,
    string ColonyId,
    string? SessionId,
    string SnapshotId,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    int AttemptCount);

public interface ISnapshotPostUploadProcessor
{
    string Id { get; }

    SnapshotPostUploadStage Stage { get; }

    int Order { get; }

    SnapshotPostUploadFailureMode FailureMode { get; }

    SnapshotPostUploadExecutionMode ExecutionMode { get; }

    bool Supports(SnapshotPostUploadKind kind);
}

public interface IInlineSnapshotPostUploadProcessor : ISnapshotPostUploadProcessor
{
    void Process(SnapshotPostUploadContext context);
}

public interface IDeferredSnapshotPostUploadProcessor : ISnapshotPostUploadProcessor
{
    string CapturePayload(SnapshotPostUploadContext context);

    void ProcessDeferred(SnapshotPostUploadDeferredContext context);
}

public interface IScheduledDeferredSnapshotPostUploadProcessor : IDeferredSnapshotPostUploadProcessor
{
    SnapshotPostUploadJobRecord Schedule(SnapshotPostUploadContext context);
}

public interface IRecoverableDeferredSnapshotPostUploadProcessor : IDeferredSnapshotPostUploadProcessor
{
    void RecoverPrepared(
        ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job,
        DateTimeOffset nowUtc);
}

public interface IDeferredSnapshotPostUploadCompletionHandler : IDeferredSnapshotPostUploadProcessor
{
    void OnDeferredCompleted(SnapshotPostUploadDeferredContext context);
}

public interface IDeferredSnapshotPostUploadArtifactReconciler : IDeferredSnapshotPostUploadProcessor
{
    void ReconcileArtifacts(ClashOfRimNetworkState state);
}

public sealed class SnapshotPostUploadManualReviewException : Exception
{
    public SnapshotPostUploadManualReviewException(string message)
        : base(message)
    {
    }
}

public static class SnapshotPostUploadPipeline
{
    public static void Run(
        SnapshotPostUploadContext context,
        IEnumerable<ISnapshotPostUploadProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(processors);

        List<ISnapshotPostUploadProcessor> registered = processors
            .Where(processor => processor is not null)
            .ToList();
        ValidateRegistrations(registered);
        List<ISnapshotPostUploadProcessor> applicable = registered
            .Where(processor => processor.Supports(context.Kind))
            .ToList();

        foreach (ISnapshotPostUploadProcessor processor in applicable
            .OrderBy(processor => processor.Stage)
            .ThenBy(processor => processor.Order)
            .ThenBy(processor => processor.Id, StringComparer.Ordinal))
        {
            ExecuteProcessor(context, processor);
        }
    }

    public static void ValidateRegistrations(IReadOnlyCollection<ISnapshotPostUploadProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        if (processors.Any(processor => string.IsNullOrWhiteSpace(processor.Id)))
        {
            throw new InvalidOperationException("Snapshot post-upload processor IDs cannot be empty.");
        }

        string? duplicateId = processors
            .GroupBy(processor => processor.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate snapshot post-upload processor ID '{duplicateId}'. Processor IDs must be globally unique.");
        }
    }

    private static void ExecuteProcessor(
        SnapshotPostUploadContext context,
        ISnapshotPostUploadProcessor processor)
    {
        try
        {
            if (processor.ExecutionMode == SnapshotPostUploadExecutionMode.Deferred)
            {
                if (processor is not IDeferredSnapshotPostUploadProcessor deferredProcessor)
                {
                    throw new InvalidOperationException(
                        $"Deferred snapshot post-upload processor '{processor.Id}' does not implement {nameof(IDeferredSnapshotPostUploadProcessor)}.");
                }

                if (deferredProcessor is IScheduledDeferredSnapshotPostUploadProcessor scheduledProcessor)
                {
                    scheduledProcessor.Schedule(context);
                }
                else
                {
                    string payloadJson = deferredProcessor.CapturePayload(context);
                    context.State.SnapshotPostUploadJobs.Enqueue(processor.Id, context, payloadJson);
                }
            }
            else
            {
                if (processor is not IInlineSnapshotPostUploadProcessor inlineProcessor)
                {
                    throw new InvalidOperationException(
                        $"Inline snapshot post-upload processor '{processor.Id}' does not implement {nameof(IInlineSnapshotPostUploadProcessor)}.");
                }

                inlineProcessor.Process(context);
            }
        }
        catch (Exception ex)
        {
            context.State.RuntimeLogger.LogError(
                ex,
                "Snapshot post-upload processor failed: processor={ProcessorId} stage={Stage} kind={Kind} snapshot={SnapshotId}",
                processor.Id,
                processor.Stage,
                context.Kind,
                context.Snapshot?.Identity.SnapshotId);
            if (processor.FailureMode == SnapshotPostUploadFailureMode.AbortPipeline)
            {
                throw;
            }
        }
    }
}
