namespace AIRsLight.ClashOfRim.Network;

// Public plugin compatibility only. Scheduling and recovery are owned by the raid module.
public static class RaidSettlementDeferredScheduler
{
    public const string ProcessorId = Modules.Raids.RaidSettlementWork.ProcessorId;

    public static SnapshotPostUploadJobRecord Schedule(ClashOfRimNetworkState state, RaidSettlementScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RaidEvidenceScheduling.Schedule(RaidEvidenceRequestAdapter.From(request));
    }

    public static SnapshotPostUploadJobRecord RecoverPrepared(ClashOfRimNetworkState state, SnapshotPostUploadJobRecord job, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RaidEvidenceScheduling.RecoverPrepared(job, nowUtc);
    }
}
