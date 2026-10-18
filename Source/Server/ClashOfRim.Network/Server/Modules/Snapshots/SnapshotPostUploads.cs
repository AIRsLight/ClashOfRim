using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotPostUploads(CoreSnapshotPostUpload core, SnapshotPostUploadRunner runner,
    ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline, ISnapshotPostUploadPlugins pluginCalls,
    ISnapshotPostUploadProcessor raidProcessor, RaidEvidenceScheduling raids) : ISnapshotCommitEffects, IRaidEvidencePipeline
{
    internal IReadOnlyList<ISnapshotPostUploadProcessor> ResolveProcessors() =>
        CoreProcessors().Concat(plugins.ActiveSnapshotPostUploadProcessors(baseline.Current)).ToArray();

    internal void ValidateRegistrations() =>
        SnapshotPostUploadRunner.ValidateRegistrations(CoreProcessors().Concat(plugins.SnapshotPostUploadProcessors).ToArray());

    private IEnumerable<ISnapshotPostUploadProcessor> CoreProcessors() =>
        core.Operations.Select(operation => operation.Processor).Append(raidProcessor);

    internal void Run(SnapshotPostUploadInput input)
    {
        var raid = new SnapshotPostUploadOperation(raidProcessor, Schedule: value => raids.SchedulePostUpload(value));
        var extensions = plugins.ActiveSnapshotPostUploadProcessors(baseline.Current);
        runner.Run(input, core.Operations.Append(raid).Concat(pluginCalls.Bind(input, extensions)));
    }

    public void AfterCommit(LatestSnapshotRecord snapshot, string? uploadKind) =>
        Run(CommittedInput(snapshot, uploadKind, new(uploadKind, null, null), true));

    internal void AfterOrdinaryUpload(LatestSnapshotRecord snapshot, string? uploadKind, SnapshotUploadMutation mutation) =>
        Run(CommittedInput(snapshot, uploadKind, new(uploadKind, mutation.ConfirmationOperation, mutation.AchievementCandidates),
            mutation.ConfirmationOperation != SnapshotConfirmationOperations.ColonyRelocation));

    private static SnapshotPostUploadInput CommittedInput(LatestSnapshotRecord snapshot, string? uploadKind,
        SnapshotPostUploadExtraData extraData, bool registerColony) => new(
            string.Equals(uploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal)
                ? SnapshotPostUploadKind.RaidSettlementEvidence : SnapshotPostUploadKind.AuthoritativeColonySnapshot,
            snapshot, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null, snapshot.AcceptedAtUtc, extraData, registerColony);

    public void Run(RaidEvidenceSchedule evidence, RaidEvidenceSchedulePreparation? preparation)
    {
        var job = evidence.Job;
        Run(new(job.Kind, evidence.Snapshot, job.UserId, job.ColonyId, job.SessionId, job.OccurredAtUtc,
            new SnapshotPostUploadExtraData(SnapshotUploadKinds.RaidSettlementEvidence, null, null)
            {
                RaidSettlement = new(evidence.RaidEventId, evidence.EvidencePayload, evidence.Origin,
                    evidence.ClientApplicationResult, evidence.RequestHash) { PreparedSchedule = preparation }
            }, false));
    }
}
