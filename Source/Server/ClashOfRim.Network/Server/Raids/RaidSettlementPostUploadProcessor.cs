using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RaidSettlementPostUploadProcessor :
    IScheduledDeferredSnapshotPostUploadProcessor,
    IRecoverableDeferredSnapshotPostUploadProcessor,
    IDeferredSnapshotPostUploadCompletionHandler,
    IDeferredSnapshotPostUploadArtifactReconciler
{
    public string Id => RaidSettlementDeferredScheduler.ProcessorId;

    public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;

    public int Order => 300;

    public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;

    public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Deferred;

    public bool Supports(SnapshotPostUploadKind kind)
    {
        return kind == SnapshotPostUploadKind.RaidSettlementEvidence;
    }

    public SnapshotPostUploadJobRecord Schedule(SnapshotPostUploadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RaidSettlementPostUploadData data = context.ExtraData.RaidSettlement
            ?? throw new InvalidOperationException("Raid settlement post-upload data is missing.");
        return RaidSettlementDeferredScheduler.Schedule(
            context.State,
            new RaidSettlementScheduleRequest(
                data.RaidEventId,
                context,
                data.EvidencePayload,
                data.Origin,
                data.ClientApplicationResult));
    }

    public string CapturePayload(SnapshotPostUploadContext context)
    {
        throw new InvalidOperationException("Raid settlement payloads must be scheduled transactionally.");
    }

    public void ProcessDeferred(SnapshotPostUploadDeferredContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RaidSettlementDeferredPayload payload = RaidSettlementDeferredPayload.Deserialize(context.PayloadJson);
        if (context.State.Ledger.Find(payload.RaidEventId)?.Status == Events.ServerEventStatus.AppliedToSnapshot)
        {
            return;
        }

        Save.SaveSnapshotPackage evidence = context.State.SnapshotPostUploadArtifacts.Read(payload.EvidenceArtifactId)
            ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found.");
        RaidSettlementOperationResult result = RaidSettlementOperationExecutor.Execute(
            context.State,
            payload,
            evidence,
            DateTimeOffset.UtcNow);
        if (result.Kind is RaidSettlementOperationResultKind.Completed
            or RaidSettlementOperationResultKind.AlreadyCompleted)
        {
            return;
        }

        throw new SnapshotPostUploadManualReviewException(
            result.Message ?? $"Raid settlement '{payload.RaidEventId}' requires manual review.");
    }

    public void RecoverPrepared(
        ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job,
        DateTimeOffset nowUtc)
    {
        RaidSettlementDeferredScheduler.RecoverPrepared(state, job, nowUtc);
    }

    public void OnDeferredCompleted(SnapshotPostUploadDeferredContext context)
    {
        RaidSettlementDeferredPayload payload = RaidSettlementDeferredPayload.Deserialize(context.PayloadJson);
        context.State.SnapshotPostUploadArtifacts.Delete(payload.EvidenceArtifactId);
    }

    public void ReconcileArtifacts(ClashOfRimNetworkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        HashSet<string> referencedArtifactIds = state.SnapshotPostUploadJobs.ListAll()
            .Where(job => string.Equals(job.ProcessorId, Id, StringComparison.Ordinal))
            .Select(job => RaidSettlementDeferredPayload.Deserialize(job.PayloadJson).EvidenceArtifactId)
            .Where(artifactId => !string.IsNullOrWhiteSpace(artifactId))
            .ToHashSet(StringComparer.Ordinal);
        state.SnapshotPostUploadArtifacts.DeleteUnreferenced(referencedArtifactIds);
    }
}
