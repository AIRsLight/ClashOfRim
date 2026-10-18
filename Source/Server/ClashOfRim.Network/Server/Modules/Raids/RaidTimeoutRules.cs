using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidTimeoutRules
{
    internal static bool SameSource(AuthoritativeEvent? current, AuthoritativeEvent expected)
        => current is not null && current.Status == expected.Status && RaidSettlementSnapshotMutation.SameSource(current, expected);

    internal static bool MatchesJob(AuthoritativeEvent raid, SnapshotPostUploadJobRecord job)
    {
        var scheduled = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
        return job.ProcessorId == RaidSettlementWork.ProcessorId
            && job.Kind == SnapshotPostUploadKind.RaidSettlementEvidence
            && job.UserId == raid.Actor.UserId && job.ColonyId == raid.Actor.ColonyId
            && scheduled.RaidEventId == raid.EventId && scheduled.AttackerUserId == raid.Actor.UserId
            && scheduled.AttackerColonyId == raid.Actor.ColonyId && scheduled.DefenderUserId == raid.Target.UserId
            && scheduled.DefenderColonyId == raid.Target.ColonyId && job.SnapshotId == scheduled.EvidenceSnapshotId
            && raid.Payload is RaidEventPayload payload && payload.DefenderSnapshotId == scheduled.DefenderSnapshotId;
    }

    internal static bool CanFinish(SnapshotPostUploadJobRegistry jobs, AuthoritativeEvent raid)
        => raid.Status == ServerEventStatus.AppliedToSnapshot
            || raid.Payload is not RaidEventPayload { TimeoutWork: { AttackerWasOffline: true } }
            || jobs.Find("raid-settlement:" + raid.EventId) is { State: SnapshotPostUploadJobState.Ready } job
                && MatchesJob(raid, job);

}
