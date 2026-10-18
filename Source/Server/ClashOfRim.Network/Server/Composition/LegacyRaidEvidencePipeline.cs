using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

// The plugin API still requires State. Only context conversion crosses this temporary boundary;
// the general post-upload pipeline moves with the remaining snapshot lifecycle in M9.
internal sealed class LegacyRaidEvidencePipeline(ClashOfRimNetworkState state) : IRaidEvidencePipeline
{
    public void Run(RaidEvidenceSchedule evidence, RaidEvidenceSchedulePreparation? preparation)
    {
        var job = evidence.Job;
        var context = new SnapshotPostUploadContext(state, job.Kind, evidence.Snapshot, job.UserId, job.ColonyId,
            job.SessionId, job.OccurredAtUtc, new SnapshotPostUploadExtraData(SnapshotUploadKinds.RaidSettlementEvidence, null, null)
            {
                RaidSettlement = new(evidence.RaidEventId, evidence.EvidencePayload, evidence.Origin,
                    evidence.ClientApplicationResult, evidence.RequestHash) { PreparedSchedule = preparation }
            }, false);
        ClashOfRimNetworkServer.RunSnapshotPostUploadProcessors(state, context);
    }
}
