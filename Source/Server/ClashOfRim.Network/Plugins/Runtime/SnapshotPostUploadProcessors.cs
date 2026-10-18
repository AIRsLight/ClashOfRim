using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

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
    public static void Run(SnapshotPostUploadContext context, IEnumerable<ISnapshotPostUploadProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(processors);
        var operations = processors.Where(processor => processor is not null)
            .Select(processor => SnapshotPostUploadPluginAdapter.Bind(processor, context));
        new SnapshotPostUploadRunner(context.State.SnapshotPostUploadJobs, () => context.State.RuntimeLogger)
            .Run(SnapshotPostUploadPluginAdapter.Input(context), operations);
    }

    public static void ValidateRegistrations(IReadOnlyCollection<ISnapshotPostUploadProcessor> processors) =>
        SnapshotPostUploadRunner.ValidateRegistrations(processors);
}
