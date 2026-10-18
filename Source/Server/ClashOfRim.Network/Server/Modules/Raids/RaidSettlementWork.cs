using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidSettlementWork(RaidSettlement settlement, ISnapshotPostUploadArtifactStore artifacts,
    SnapshotPostUploadJobRegistry jobs, SnapshotMutationCoordinator mutations)
{
    internal const string ProcessorId = "core.raid-settlement";

    internal void Process(RaidSettlementDeferredPayload payload, DateTimeOffset nowUtc)
    {
        // Settlement owns recovery and checks completion before reading disposable evidence.
        var result = settlement.Execute(payload, () => artifacts.Read(payload.EvidenceArtifactId)
            ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found."), nowUtc);
        if (result.Kind is RaidSettlementOperationResultKind.Completed or RaidSettlementOperationResultKind.AlreadyCompleted) return;
        throw new SnapshotPostUploadManualReviewException(result.Message ?? $"Raid settlement '{payload.RaidEventId}' requires manual review.");
    }

    internal void Complete(RaidSettlementDeferredPayload payload) => artifacts.Delete(payload.EvidenceArtifactId);

    internal void ReconcileArtifacts()
    {
        using var mutation = mutations.EnterUnrecovered();
        var referenced = jobs.ListAll().Where(job => job.ProcessorId == ProcessorId)
            .Select(job => RaidSettlementDeferredPayload.Deserialize(job.PayloadJson).EvidenceArtifactId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        artifacts.DeleteUnreferenced(referenced);
    }
}
