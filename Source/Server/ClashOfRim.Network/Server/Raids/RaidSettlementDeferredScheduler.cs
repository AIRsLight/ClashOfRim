using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static class RaidSettlementDeferredScheduler
{
    public const string ProcessorId = "core.raid-settlement";

    public static SnapshotPostUploadJobRecord Schedule(
        ClashOfRimNetworkState state,
        RaidSettlementScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RaidEventId);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.EvidencePayload);
        if (request.Context.Kind != SnapshotPostUploadKind.RaidSettlementEvidence)
        {
            throw new InvalidOperationException("Raid settlement can only be scheduled from raid settlement evidence.");
        }

        AuthoritativeEvent raid = state.Ledger.Find(request.RaidEventId)
            ?? throw new InvalidOperationException($"Raid event '{request.RaidEventId}' was not found.");
        if (raid.Type != ServerEventType.Raid
            || raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } raidPayload
            || !raidPayload.RequiresSettlement)
        {
            throw new InvalidOperationException($"Event '{request.RaidEventId}' is not a player raid requiring settlement.");
        }

        string attackerColonyId = raid.Actor.ColonyId ?? request.Context.ColonyId;
        string defenderColonyId = raid.Target.ColonyId
            ?? throw new InvalidOperationException("Raid defender colony ID is missing.");
        string evidenceSnapshotId = request.Context.Snapshot.Identity.SnapshotId
            ?? throw new InvalidOperationException("Raid settlement evidence snapshot ID is missing.");
        string jobId = "raid-settlement:" + raid.EventId;
        SnapshotPostUploadJobRecord? existing = state.SnapshotPostUploadJobs.Find(jobId);
        if (existing is not null)
        {
            return existing;
        }

        string artifactId = BuildArtifactId(raid.EventId, evidenceSnapshotId);
        var evidencePackage = new SaveSnapshotPackage(
            request.Context.Snapshot.Envelope,
            request.EvidencePayload,
            request.Context.Snapshot.Index);
        var payload = new RaidSettlementDeferredPayload(
            raid.EventId,
            raid.Actor.UserId,
            attackerColonyId,
            raid.Target.UserId,
            defenderColonyId,
            evidenceSnapshotId,
            raidPayload.DefenderSnapshotId,
            artifactId,
            request.Origin,
            request.ClientApplicationResult);

        state.SnapshotPostUploadArtifacts.Store(artifactId, evidencePackage);
        bool jobPersisted = false;
        try
        {
            SnapshotPostUploadJobRecord prepared = state.SnapshotPostUploadJobs.EnqueuePrepared(
                jobId,
                ProcessorId,
                request.Context,
                payload.Serialize());
            jobPersisted = true;

            if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
            {
                throw new InvalidOperationException("Raid settlement requires a snapshot package store.");
            }

            lock (state.RaidSettlementSnapshotMutationGate)
            {
                packageStore.StoreLatest(
                    evidencePackage,
                    evidencePackage.Index,
                    request.Context.OccurredAtUtc);
                state.Players.RecordLatestSnapshotReference(
                    raid.Actor.UserId,
                    attackerColonyId,
                    evidenceSnapshotId,
                    request.Context.OccurredAtUtc);
            }

            return state.SnapshotPostUploadJobs.MarkReady(prepared.JobId, request.Context.OccurredAtUtc);
        }
        catch
        {
            if (jobPersisted)
            {
                state.SnapshotPostUploadJobs.MarkCompleted(jobId);
            }

            state.SnapshotPostUploadArtifacts.Delete(artifactId);
            throw;
        }
    }

    private static string BuildArtifactId(string raidEventId, string evidenceSnapshotId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raidEventId + "\u001f" + evidenceSnapshotId));
        return "raid-settlement-" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
