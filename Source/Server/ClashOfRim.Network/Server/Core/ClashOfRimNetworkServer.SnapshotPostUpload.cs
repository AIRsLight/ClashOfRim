using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static readonly IReadOnlyList<ISnapshotPostUploadProcessor> CoreSnapshotPostUploadProcessors =
        new ISnapshotPostUploadProcessor[]
        {
            InlineSnapshotPostUploadProcessor.Create(
                "core.latest-snapshot-reference",
                SnapshotPostUploadStage.AuthoritativeProjection,
                order: 100,
                SnapshotPostUploadFailureMode.AbortPipeline,
                SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                context => context.State.Players.RecordLatestSnapshotReference(
                    context.UserId, context.ColonyId, context.Snapshot.Identity.SnapshotId, context.OccurredAtUtc)),
            InlineSnapshotPostUploadProcessor.Create(
                "core.world-tile-float-layers",
                SnapshotPostUploadStage.AuthoritativeProjection,
                order: 200,
                SnapshotPostUploadFailureMode.AbortPipeline,
                SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                context => context.State.WorldLayers.Project(context.UserId, context.ColonyId,
                    AcceptedUpload(context).AcceptedSnapshot, context.OccurredAtUtc)),
            InlineSnapshotPostUploadProcessor.Create(
                "core.colony-site-and-world-extensions",
                SnapshotPostUploadStage.AuthoritativeProjection,
                order: 300,
                SnapshotPostUploadFailureMode.AbortPipeline,
                SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                context =>
                {
                    if (context.RegisterPlayerColonySite)
                    {
                        context.State.ColonyProjection.Apply(
                            context.UserId,
                            context.ColonyId,
                            AcceptedUpload(context));
                    }
                }),
            InlineSnapshotPostUploadProcessor.Create(
                "core.snapshot-metrics-and-achievements",
                SnapshotPostUploadStage.DerivedMetrics,
                order: 100,
                SnapshotPostUploadFailureMode.AbortPipeline,
                SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                context => context.State.SnapshotAchievements.Record(
                    context.UserId,
                    context.ColonyId,
                    context.Snapshot,
                    context.OccurredAtUtc,
                    context.ExtraData.AchievementCandidates)),
            InlineSnapshotPostUploadProcessor.Create(
                "core.support-pawn-deaths",
                SnapshotPostUploadStage.EventReconciliation,
                order: 100,
                SnapshotPostUploadFailureMode.AbortPipeline,
                new[]
                {
                    SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                    SnapshotPostUploadKind.RaidSettlementEvidence
                },
                context => context.State.SupportDeaths.Process(AcceptedUpload(context))),
            InlineSnapshotPostUploadProcessor.Create(
                "core.pending-operation-confirmation",
                SnapshotPostUploadStage.EventReconciliation,
                order: 200,
                SnapshotPostUploadFailureMode.AbortPipeline,
                new[]
                {
                    SnapshotPostUploadKind.AuthoritativeColonySnapshot,
                    SnapshotPostUploadKind.RaidSettlementEvidence
                },
                ConfirmPendingOperationsFromSnapshot),
            new RaidSettlementPostUploadProcessor()
        };

    private static IReadOnlyList<ISnapshotPostUploadProcessor> ResolveSnapshotPostUploadProcessors(
        ClashOfRimNetworkState state)
    {
        return CoreSnapshotPostUploadProcessors
            .Concat(state.Plugins.ActiveSnapshotPostUploadProcessors(state.CompatibilityBaseline.Current))
            .ToList();
    }

    private static void RunSnapshotPostUploadProcessors(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? sessionId,
        SnapshotUploadResult upload,
        DateTimeOffset nowUtc,
        SnapshotPostUploadExtraData? extraData = null,
        bool registerPlayerColonySite = true,
        IReadOnlyCollection<SnapshotAchievementCandidateDto>? achievementCandidates = null)
    {
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return;
        }

        extraData ??= new SnapshotPostUploadExtraData(upload.SnapshotUploadKind, null, null);
        if (achievementCandidates is not null)
        {
            extraData = extraData with { AchievementCandidates = achievementCandidates };
        }
        else if (string.IsNullOrWhiteSpace(extraData.SnapshotUploadKind)
            && !string.IsNullOrWhiteSpace(upload.SnapshotUploadKind))
        {
            extraData = extraData with { SnapshotUploadKind = upload.SnapshotUploadKind };
        }

        RunSnapshotPostUploadProcessors(
            state,
            new SnapshotPostUploadContext(
                state,
                ResolveSnapshotPostUploadKind(extraData.SnapshotUploadKind),
                upload.AcceptedSnapshot,
                userId,
                colonyId,
                sessionId,
                nowUtc,
                extraData,
                registerPlayerColonySite));
    }

    internal static void RunSnapshotPostUploadProcessors(
        ClashOfRimNetworkState state,
        SnapshotPostUploadContext context)
    {
        SnapshotPostUploadPipeline.Run(context, ResolveSnapshotPostUploadProcessors(state));
    }

    private static SnapshotUploadResult AcceptedUpload(SnapshotPostUploadContext context)
    {
        return SnapshotUploadResult.Accept(context.Snapshot, context.ExtraData.SnapshotUploadKind);
    }

    private static SnapshotPostUploadKind ResolveSnapshotPostUploadKind(string? snapshotUploadKind)
    {
        return string.Equals(snapshotUploadKind, SnapshotUploadKinds.RaidSettlementEvidence, StringComparison.Ordinal)
            ? SnapshotPostUploadKind.RaidSettlementEvidence
            : SnapshotPostUploadKind.AuthoritativeColonySnapshot;
    }

    private static void ConfirmPendingOperationsFromSnapshot(SnapshotPostUploadContext context)
    {
        using (context.State.EnterSnapshotMutation())
            ConfirmPendingOperationsFromSnapshotLocked(context);
    }

    private static void ConfirmPendingOperationsFromSnapshotLocked(SnapshotPostUploadContext context)
    {
        string? snapshotId = context.Snapshot.Identity.SnapshotId;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        LatestSnapshotRecord? latest = context.State.SnapshotStore.GetLatest(context.UserId, context.ColonyId);
        if (!string.Equals(latest?.Identity.SnapshotId, snapshotId, StringComparison.Ordinal))
        {
            return;
        }

        // Confirmation is captured and committed with snapshot publication. Never rescan here:
        // a later request may have created new pending work after this snapshot was accepted.
        context.State.MercenaryIncidents.Recover();
    }

    private sealed class InlineSnapshotPostUploadProcessor : IInlineSnapshotPostUploadProcessor
    {
        private readonly IReadOnlySet<SnapshotPostUploadKind> supportedKinds;
        private readonly Action<SnapshotPostUploadContext> process;

        private InlineSnapshotPostUploadProcessor(
            string id,
            SnapshotPostUploadStage stage,
            int order,
            SnapshotPostUploadFailureMode failureMode,
            IReadOnlyCollection<SnapshotPostUploadKind> supportedKinds,
            Action<SnapshotPostUploadContext> process)
        {
            Id = id;
            Stage = stage;
            Order = order;
            FailureMode = failureMode;
            this.supportedKinds = supportedKinds.ToHashSet();
            this.process = process;
        }

        public string Id { get; }

        public SnapshotPostUploadStage Stage { get; }

        public int Order { get; }

        public SnapshotPostUploadFailureMode FailureMode { get; }

        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;

        public static InlineSnapshotPostUploadProcessor Create(
            string id,
            SnapshotPostUploadStage stage,
            int order,
            SnapshotPostUploadFailureMode failureMode,
            SnapshotPostUploadKind supportedKind,
            Action<SnapshotPostUploadContext> process)
        {
            return Create(id, stage, order, failureMode, new[] { supportedKind }, process);
        }

        public static InlineSnapshotPostUploadProcessor Create(
            string id,
            SnapshotPostUploadStage stage,
            int order,
            SnapshotPostUploadFailureMode failureMode,
            IReadOnlyCollection<SnapshotPostUploadKind> supportedKinds,
            Action<SnapshotPostUploadContext> process)
        {
            return new InlineSnapshotPostUploadProcessor(
                id,
                stage,
                order,
                failureMode,
                supportedKinds,
                process);
        }

        public bool Supports(SnapshotPostUploadKind kind)
        {
            return supportedKinds.Contains(kind);
        }

        public void Process(SnapshotPostUploadContext context)
        {
            process(context);
        }
    }
}
