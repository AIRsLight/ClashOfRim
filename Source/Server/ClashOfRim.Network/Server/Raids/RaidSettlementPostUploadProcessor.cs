using AIRsLight.ClashOfRim.Network.Plugins;

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
        if (data.PreparedSchedule is { } prepared)
            return prepared.Commit(context.State.RaidEvidenceScheduling, context.Snapshot.Envelope, context.OccurredAtUtc);
        return context.State.RaidEvidenceScheduling.Schedule(RaidEvidenceRequestAdapter.From(new RaidSettlementScheduleRequest(
            data.RaidEventId, context, data.EvidencePayload, data.Origin, data.ClientApplicationResult, data.RequestHash)));
    }

    public string CapturePayload(SnapshotPostUploadContext context)
    {
        throw new InvalidOperationException("Raid settlement payloads must be scheduled transactionally.");
    }

    public void ProcessDeferred(SnapshotPostUploadDeferredContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.State.RaidSettlementWork.Process(RaidSettlementDeferredPayload.Deserialize(context.PayloadJson), DateTimeOffset.UtcNow);
    }

    public void RecoverPrepared(
        ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job,
        DateTimeOffset nowUtc)
    {
        state.RaidEvidenceScheduling.RecoverPrepared(job, nowUtc);
    }

    public void OnDeferredCompleted(SnapshotPostUploadDeferredContext context)
    {
        context.State.RaidSettlementWork.Complete(RaidSettlementDeferredPayload.Deserialize(context.PayloadJson));
    }

    public void ReconcileArtifacts(ClashOfRimNetworkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.RaidSettlementWork.ReconcileArtifacts();
    }
}
