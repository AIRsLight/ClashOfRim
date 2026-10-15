using Microsoft.Extensions.Logging;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // The caller must not hold the mutation gate: reading and preparing evidence can wait for a slot.
    private static SnapshotPostUploadJobRecord? ScheduleOfflineTimedOutRaid(
        ClashOfRimNetworkState state, AuthoritativeEvent raid, DateTimeOffset nowUtc)
    {
        if (state.SnapshotStore is not IColonySnapshotPackageStore packages
            || raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player,
                TimeoutWork: { AttackerWasOffline: true, Completed: false } } source
            || string.IsNullOrWhiteSpace(raid.Actor.UserId) || string.IsNullOrWhiteSpace(raid.Target.UserId)
            || string.IsNullOrWhiteSpace(raid.Actor.ColonyId) || string.IsNullOrWhiteSpace(raid.Target.ColonyId)) return null;

        string owner = raid.Actor.UserId, colony = raid.Actor.ColonyId;
        string defender = raid.Target.UserId, defenseColony = raid.Target.ColonyId;
        string jobId = "raid-settlement:" + raid.EventId;
        SaveSnapshotEnvelope? attackerVersion, defenderVersion;
        SnapshotPostUploadJobRecord? existing;
        string? artifactId = null;
        using (EnterRecoveredSnapshotMutation(state))
        {
            lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
            {
                if (!SameRaidCleanupSource(state.Ledger.Find(raid.EventId), raid)) return null;
                existing = state.SnapshotPostUploadJobs.Find(jobId);
                if (existing is null && (source.Settlement is not null || source.ReturnedSnapshotId is not null)) return null;
                attackerVersion = packages.GetLatest(owner, colony)?.Envelope;
                defenderVersion = packages.GetLatest(defender, defenseColony)?.Envelope;
                if (existing is null && attackerVersion?.Identity.SnapshotId is { } snapshotId)
                {
                    artifactId = RaidSettlementDeferredScheduler.BuildArtifactId(raid.EventId, snapshotId);
                    // Only an unreferenced orphan may be replaced. Concurrent preparations are invalidated.
                    state.SnapshotPostUploadArtifacts.Delete(artifactId);
                }
            }
        }
        if (existing is not null)
        {
            if (!MatchesRaidSettlementJob(raid, existing))
                throw new InvalidDataException("Timed-out raid has a conflicting settlement job.");
            if (existing.State == SnapshotPostUploadJobState.Prepared)
                existing = RaidSettlementDeferredScheduler.RecoverPrepared(state, existing, nowUtc);
            return existing.State == SnapshotPostUploadJobState.Ready ? existing : null;
        }

        var captured = SnapshotPreparationService.Run(() =>
            (Attacker: packages.GetLatestPackage(owner, colony), Defender: packages.GetLatestPackage(defender, defenseColony)));
        var evidence = captured.Attacker;
        bool missing = evidence is null || captured.Defender is null
            || captured.Defender.Envelope.Identity.SnapshotId != source.DefenderSnapshotId;
        // The read may itself have crossed a publication. Never prepare a package from a different version.
        if (!missing && (!RaidSettlementDeferredScheduler.SameVersion(evidence!.Envelope, attackerVersion)
            || !RaidSettlementDeferredScheduler.SameVersion(captured.Defender!.Envelope, defenderVersion))) return null;

        using var prepared = missing ? null : SnapshotPreparationService.Run(() =>
        {
            if (evidence!.Envelope.Identity.OwnerId != owner || evidence.Envelope.Identity.ColonyId != colony)
                throw new InvalidDataException("Offline evidence does not belong to the attacker.");
            RaidSettlementDeferredScheduler.ValidatePayload(evidence);
            return (state.SnapshotPostUploadArtifacts as IPreparedSnapshotPostUploadArtifactStore)?.Prepare(artifactId!, evidence!);
        });
        using (EnterRecoveredSnapshotMutation(state))
        {
            lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
            {
                if (!SameRaidCleanupSource(state.Ledger.Find(raid.EventId), raid)
                    || !RaidSettlementDeferredScheduler.SameVersion(packages.GetLatest(owner, colony)?.Envelope, attackerVersion)
                    || !RaidSettlementDeferredScheduler.SameVersion(packages.GetLatest(defender, defenseColony)?.Envelope, defenderVersion)
                    || FindActiveSourceRaidForAttacker(state, owner, colony) is not null) return null;
                existing = state.SnapshotPostUploadJobs.Find(jobId);
                if (existing is not null)
                {
                    if (!MatchesRaidSettlementJob(raid, existing))
                        throw new InvalidDataException("Timed-out raid has a conflicting settlement job.");
                    return existing.State == SnapshotPostUploadJobState.Ready ? existing : null;
                }
                if (missing)
                {
                    state.RuntimeLogger.LogWarning("Offline raid timeout settlement missing snapshot: raid={RaidEventId}", raid.EventId);
                    state.Ledger.ReportApplicationResult(raid.EventId, EventApplicationResultKind.NeedsManualReview,
                        T("Raid.OfflineTimeoutSettlementMissingSnapshot"), nextRetryAtUtc: null);
                    return null;
                }
                if (prepared is not null)
                {
                    if (!prepared.TryPublish()) return null;
                }
                else state.SnapshotPostUploadArtifacts.Store(artifactId!, evidence!);

                var context = new SnapshotPostUploadContext(state, SnapshotPostUploadKind.RaidSettlementEvidence,
                    new LatestSnapshotRecord(evidence!.Envelope.Identity, evidence.Envelope, evidence.Index, nowUtc),
                    owner, colony, null, nowUtc, SnapshotPostUploadExtraData.Empty, RegisterPlayerColonySite: false);
                var payload = new RaidSettlementDeferredPayload(raid.EventId, owner, colony, defender, defenseColony,
                    evidence.Envelope.Identity.SnapshotId!, source.DefenderSnapshotId, artifactId!,
                    RaidSettlementOrigin.OfflineTimeout, "OfflineTimeout", new RaidEvidencePublication(attackerVersion, evidence.Envelope));
                var job = state.SnapshotPostUploadJobs.EnqueuePrepared(jobId, RaidSettlementDeferredScheduler.ProcessorId, context, payload.Serialize());
                // The evidence and Prepared intent now survive every subsequent failure.
                return RaidSettlementDeferredScheduler.CompletePreparedEvidence(state, job, payload, evidence, nowUtc);
            }
        }
    }
}
