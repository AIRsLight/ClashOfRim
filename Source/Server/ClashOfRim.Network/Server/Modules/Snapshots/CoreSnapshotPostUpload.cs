using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class CoreSnapshotPostUpload
{
    internal IReadOnlyList<SnapshotPostUploadOperation> Operations { get; }

    internal CoreSnapshotPostUpload(PlayerRegistry players, SnapshotWorldLayers worldLayers, SnapshotColonyProjection colonies,
        SnapshotAchievements achievements, SupportSnapshotDeaths supportDeaths, IColonySnapshotIndexStore snapshots,
        SnapshotMutationCoordinator mutations, MercenaryIncidentRecovery incidents)
    {
        Operations =
        [
            Inline("core.latest-snapshot-reference", SnapshotPostUploadStage.AuthoritativeProjection, 100,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot], input =>
                    players.RecordLatestSnapshotReference(input.UserId, input.ColonyId, input.Snapshot.Identity.SnapshotId, input.OccurredAtUtc)),
            Inline("core.world-tile-float-layers", SnapshotPostUploadStage.AuthoritativeProjection, 200,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot], input =>
                    worldLayers.Project(input.UserId, input.ColonyId, input.Snapshot, input.OccurredAtUtc)),
            Inline("core.colony-site-and-world-extensions", SnapshotPostUploadStage.AuthoritativeProjection, 300,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot], input =>
                {
                    if (input.RegisterPlayerColonySite) colonies.Apply(input.UserId, input.ColonyId, input.AcceptedUpload());
                }),
            Inline("core.snapshot-metrics-and-achievements", SnapshotPostUploadStage.DerivedMetrics, 100,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot], input =>
                    achievements.Record(input.UserId, input.ColonyId, input.Snapshot, input.OccurredAtUtc, input.ExtraData.AchievementCandidates)),
            Inline("core.support-pawn-deaths", SnapshotPostUploadStage.EventReconciliation, 100,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot, SnapshotPostUploadKind.RaidSettlementEvidence], input =>
                    supportDeaths.Process(input.AcceptedUpload())),
            Inline("core.pending-operation-confirmation", SnapshotPostUploadStage.EventReconciliation, 200,
                [SnapshotPostUploadKind.AuthoritativeColonySnapshot, SnapshotPostUploadKind.RaidSettlementEvidence], input =>
                {
                    using (mutations.EnterUnrecovered())
                    {
                        string? snapshotId = input.Snapshot.Identity.SnapshotId;
                        if (string.IsNullOrWhiteSpace(snapshotId)) return;
                        var latest = snapshots.GetLatest(input.UserId, input.ColonyId);
                        if (!string.Equals(latest?.Identity.SnapshotId, snapshotId, StringComparison.Ordinal)) return;
                        // Confirmation was captured with publication. Never rescan newly created pending operations here.
                        incidents.Recover();
                    }
                })
        ];
    }

    private static SnapshotPostUploadOperation Inline(string id, SnapshotPostUploadStage stage, int order,
        IReadOnlyCollection<SnapshotPostUploadKind> kinds, Action<SnapshotPostUploadInput> process) =>
        new(new Processor(id, stage, order, kinds), process);

    private sealed class Processor(string id, SnapshotPostUploadStage stage, int order, IReadOnlyCollection<SnapshotPostUploadKind> kinds)
        : ISnapshotPostUploadProcessor
    {
        public string Id => id;
        public SnapshotPostUploadStage Stage => stage;
        public int Order => order;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => kinds.Contains(kind);
    }
}
