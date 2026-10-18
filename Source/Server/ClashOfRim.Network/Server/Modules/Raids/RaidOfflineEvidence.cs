using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Raids.RaidTimeoutRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidOfflineEvidence(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    SnapshotPostUploadJobRegistry jobs, ISnapshotPostUploadArtifactStore artifacts, SnapshotMutationCoordinator mutations,
    RaidQueries queries, RaidEvidenceScheduling scheduling, Func<ILogger> logger)
{
    internal IReadOnlySet<string> ScheduleAll(
        IReadOnlyList<AuthoritativeEvent> offlineFailedRaids,
        DateTimeOffset nowUtc)
    {
        var queuedRaidIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raid in offlineFailedRaids)
        {
            try
            {
                var job = Schedule(raid, nowUtc);
                if (job is null) continue;
                queuedRaidIds.Add(raid.EventId);
                logger().LogInformation("Queued offline raid timeout settlement: raid={RaidEventId} job={JobId} evidence={EvidenceSnapshotId}",
                    raid.EventId, job.JobId, job.SnapshotId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                logger().LogWarning(ex, "Offline raid timeout settlement could not be queued: raid={RaidEventId}", raid.EventId);
            }
        }
        return queuedRaidIds;
    }

    // The caller must not hold the mutation gate: reading and preparing evidence can wait for a slot.
    internal SnapshotPostUploadJobRecord? Schedule(
        AuthoritativeEvent raid, DateTimeOffset nowUtc)
    {
        if (snapshots is not IColonySnapshotPackageStore packages
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
        using (mutations.Enter())
        {
            lock (jobs.SnapshotCommitGate)
            {
                if (!SameSource(ledger.Find(raid.EventId), raid)) return null;
                existing = jobs.Find(jobId);
                if (existing is null && (source.Settlement is not null || source.ReturnedSnapshotId is not null)) return null;
                attackerVersion = packages.GetLatest(owner, colony)?.Envelope;
                defenderVersion = packages.GetLatest(defender, defenseColony)?.Envelope;
                if (existing is null && attackerVersion?.Identity.SnapshotId is { } snapshotId)
                {
                    artifactId = RaidEvidenceRules.BuildArtifactId(raid.EventId, snapshotId);
                    // Only an unreferenced orphan may be replaced. Concurrent preparations are invalidated.
                    artifacts.Delete(artifactId);
                }
            }
        }
        if (existing is not null)
        {
            if (!MatchesJob(raid, existing))
                throw new InvalidDataException("Timed-out raid has a conflicting settlement job.");
            if (existing.State == SnapshotPostUploadJobState.Prepared)
                existing = scheduling.RecoverPrepared(existing, nowUtc);
            return existing.State == SnapshotPostUploadJobState.Ready ? existing : null;
        }

        var captured = SnapshotPreparationService.Run(() =>
            (Attacker: packages.GetLatestPackage(owner, colony), Defender: packages.GetLatestPackage(defender, defenseColony)));
        var evidence = captured.Attacker;
        bool missing = evidence is null || captured.Defender is null
            || captured.Defender.Envelope.Identity.SnapshotId != source.DefenderSnapshotId;
        // The read may itself have crossed a publication. Never prepare a package from a different version.
        if (!missing && (!RaidEvidenceRules.SameVersion(evidence!.Envelope, attackerVersion)
            || !RaidEvidenceRules.SameVersion(captured.Defender!.Envelope, defenderVersion))) return null;

        using var prepared = missing ? null : SnapshotPreparationService.Run(() =>
        {
            if (evidence!.Envelope.Identity.OwnerId != owner || evidence.Envelope.Identity.ColonyId != colony)
                throw new InvalidDataException("Offline evidence does not belong to the attacker.");
            RaidEvidenceRules.ValidatePayload(evidence);
            return (artifacts as IPreparedSnapshotPostUploadArtifactStore)?.Prepare(artifactId!, evidence!);
        });
        using (mutations.Enter())
        {
            lock (jobs.SnapshotCommitGate)
            {
                if (!SameSource(ledger.Find(raid.EventId), raid)
                    || !RaidEvidenceRules.SameVersion(packages.GetLatest(owner, colony)?.Envelope, attackerVersion)
                    || !RaidEvidenceRules.SameVersion(packages.GetLatest(defender, defenseColony)?.Envelope, defenderVersion)
                    || queries.ActiveAttacker(owner, colony) is not null) return null;
                existing = jobs.Find(jobId);
                if (existing is not null)
                {
                    if (!MatchesJob(raid, existing))
                        throw new InvalidDataException("Timed-out raid has a conflicting settlement job.");
                    return existing.State == SnapshotPostUploadJobState.Ready ? existing : null;
                }
                if (missing)
                {
                    logger().LogWarning("Offline raid timeout settlement missing snapshot: raid={RaidEventId}", raid.EventId);
                    ledger.ReportApplicationResult(raid.EventId, EventApplicationResultKind.NeedsManualReview,
                        ServerText.Format("Raid.OfflineTimeoutSettlementMissingSnapshot"), nextRetryAtUtc: null);
                    return null;
                }
                if (prepared is not null)
                {
                    if (!prepared.TryPublish()) return null;
                }
                else artifacts.Store(artifactId!, evidence!);

                var input = new SnapshotPostUploadJobInput(SnapshotPostUploadKind.RaidSettlementEvidence,
                    owner, colony, null, evidence!.Envelope.Identity.SnapshotId, nowUtc);
                var payload = new RaidSettlementDeferredPayload(raid.EventId, owner, colony, defender, defenseColony,
                    evidence.Envelope.Identity.SnapshotId!, source.DefenderSnapshotId, artifactId!,
                    RaidSettlementOrigin.OfflineTimeout, "OfflineTimeout", new RaidEvidencePublication(attackerVersion, evidence.Envelope));
                var job = jobs.EnqueuePrepared(jobId, RaidSettlementWork.ProcessorId, input, payload.Serialize());
                // The evidence and Prepared intent now survive every subsequent failure.
                return scheduling.CompletePreparedEvidence(job, payload, evidence, nowUtc);
            }
        }
    }
}
