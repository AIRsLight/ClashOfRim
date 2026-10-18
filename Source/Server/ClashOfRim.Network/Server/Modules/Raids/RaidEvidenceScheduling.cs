using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using static AIRsLight.ClashOfRim.Network.Modules.Raids.RaidEvidenceRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidEvidenceScheduling(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    ISnapshotPostUploadArtifactStore artifacts, SnapshotPostUploadJobRegistry jobs, PlayerRegistry players, SnapshotMutationCoordinator mutations)
{
    internal bool SupportsPreparation => snapshots is IColonySnapshotPackageStore
        && artifacts is IPreparedSnapshotPostUploadArtifactStore and IVersionedSnapshotPostUploadArtifactStore;

    internal RaidEvidenceSchedulePreparation Capture(RaidEvidenceSchedule request) =>
        new(this, ledger, (IColonySnapshotPackageStore)snapshots, artifacts, jobs, mutations, request);

    internal SnapshotPostUploadJobRecord Schedule(RaidEvidenceSchedule request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using (mutations.Enter())
        {
            return ScheduleUnderMutation(request);
        }
    }

    private SnapshotPostUploadJobRecord ScheduleUnderMutation(RaidEvidenceSchedule request)
    {
        var raid = Source(ledger, request);
        var raidPayload = (RaidEventPayload)raid.Payload;
        string attackerColonyId = raid.Actor.ColonyId!;
        string defenderColonyId = raid.Target.ColonyId!;
        string evidenceSnapshotId = request.Snapshot.Identity.SnapshotId!;
        var evidencePackage = new SaveSnapshotPackage(request.Snapshot.Envelope, request.EvidencePayload, request.Snapshot.Index);
        ValidatePayload(evidencePackage);
        string jobId = "raid-settlement:" + raid.EventId;
        SnapshotPostUploadJobRecord? existing = jobs.Find(jobId);
        if (existing is not null)
        {
            RaidSettlementDeferredPayload original = RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson);
            SaveSnapshotPackage artifact = ReadEvidence(existing, original);
            if (original.RaidEventId != raid.EventId || original.DefenderUserId != raid.Target.UserId
                || original.DefenderColonyId != defenderColonyId || original.DefenderSnapshotId != raidPayload.DefenderSnapshotId
                || original.RequestHash != request.RequestHash || original.Origin != request.Origin || original.ClientApplicationResult != request.ClientApplicationResult
                || artifact.Envelope != (evidencePackage.Envelope with { NextLineageToken = artifact.Envelope.NextLineageToken }))
                throw new InvalidOperationException("Raid settlement retry differs from the prepared request.");
            return existing.State switch
            {
                SnapshotPostUploadJobState.Prepared => RecoverPreparedUnderMutation(existing, request.Job.OccurredAtUtc),
                SnapshotPostUploadJobState.Ready => existing,
                _ => throw new InvalidOperationException(
                    $"Raid settlement '{raid.EventId}' is awaiting manual review: {existing.LastError}")
            };
        }

        if (snapshots is not IColonySnapshotPackageStore packageStore)
        {
            throw new InvalidOperationException("Raid settlement requires a snapshot package store.");
        }

        SaveSnapshotPackage? defenderPackage = packageStore.GetLatestPackage(
            raid.Target.UserId,
            defenderColonyId);
        if (defenderPackage is null
            || !string.Equals(
                defenderPackage.Envelope.Identity.SnapshotId,
                raidPayload.DefenderSnapshotId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raid defender snapshot is missing or no longer matches the raid baseline.");
        }

        SaveSnapshotEnvelope? previous = packageStore.GetLatest(raid.Actor.UserId, attackerColonyId)?.Envelope;
        if (!SameVersion(previous, evidencePackage.Envelope))
        {
            if (request.Origin == RaidSettlementOrigin.OfflineTimeout
                || (previous is null && (evidencePackage.Envelope.PreviousSnapshotId is not null || evidencePackage.Envelope.LineageToken is not null))
                || (!string.IsNullOrWhiteSpace(evidencePackage.Envelope.PreviousSnapshotId)
                    && evidencePackage.Envelope.PreviousSnapshotId != previous?.Identity.SnapshotId)
                || (!string.IsNullOrWhiteSpace(previous?.NextLineageToken)
                    && evidencePackage.Envelope.LineageToken != previous.NextLineageToken))
                throw new InvalidOperationException("Raid evidence no longer follows the attacker snapshot baseline.");
        }
        string artifactId = BuildArtifactId(raid.EventId, evidenceSnapshotId);
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
            request.ClientApplicationResult,
            new RaidEvidencePublication(previous, evidencePackage.Envelope), request.RequestHash);

        // No job references this raid's artifact yet. Replace any orphan from a failed enqueue.
        artifacts.Delete(artifactId);
        artifacts.Store(artifactId, evidencePackage);
        SnapshotPostUploadJobRecord prepared = jobs.EnqueuePrepared(
            jobId,
            RaidSettlementWork.ProcessorId,
            request.Job,
            payload.Serialize());
        // A store can publish and then throw. Retain both intent and evidence on every failure.
        return RecoverPreparedUnderMutation(prepared, request.Job.OccurredAtUtc);
    }

    internal SnapshotPostUploadJobRecord RecoverPrepared(
        SnapshotPostUploadJobRecord job, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        // Online scheduling still owns an outer gate; custom stores retain their original recovery boundary.
        if (mutations.IsEntered
            || artifacts is not IVersionedSnapshotPostUploadArtifactStore versioned)
        {
            using var mutation = mutations.Enter();
            return RecoverPreparedUnderMutation(job, nowUtc);
        }

        RaidSettlementDeferredPayload payload;
        IColonySnapshotPackageStore packages;
        SaveSnapshotEnvelope? latest;
        using (mutations.Enter())
        {
            lock (jobs.SnapshotCommitGate)
            {
                var current = jobs.Find(job.JobId);
                if (current is null) return job;
                job = current;
                if (job.State != SnapshotPostUploadJobState.Prepared || job.ProcessorId != RaidSettlementWork.ProcessorId) return job;
                payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
                packages = snapshots as IColonySnapshotPackageStore
                    ?? throw new InvalidOperationException("Raid settlement requires a snapshot package store.");
                latest = packages.GetLatest(payload.AttackerUserId, payload.AttackerColonyId)?.Envelope;
            }
        }
        ISnapshotPostUploadArtifactRead? captured = null;
        SaveSnapshotPackage? evidence = null;
        PreparedSnapshotPayload? storage = null;
        Exception? preparationFailure = null;
        try
        {
            try
            {
                SnapshotPreparationService.Run(() =>
                {
                    captured = versioned.ReadCaptured(payload.EvidenceArtifactId);
                    evidence = captured.Package ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found.");
                    ValidatePayload(evidence);
                    if (!SameVersion(latest, evidence.Envelope))
                        storage = (packages as IPreparedColonySnapshotPackageStore)?.PreparePayload(evidence.Payload);
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.Xml.XmlException or System.Text.Json.JsonException) { preparationFailure = ex; }
            using (mutations.Enter())
            {
                lock (jobs.SnapshotCommitGate)
                {
                    var current = jobs.Find(job.JobId);
                    if (current is null) return job;
                    if (current != job) return current; // A removed, completed or revised task cannot be revived by old preparation.
                    if (preparationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
                    if (!captured!.IsCurrent) throw new InvalidOperationException("Raid evidence changed during recovery preparation.");
                    ValidateBinding(ledger, job, payload, evidence!);
                    if (packages is IPreparedColonySnapshotPackageStore && storage is null
                        && !SameVersion(packages.GetLatest(payload.AttackerUserId, payload.AttackerColonyId)?.Envelope, evidence!.Envelope))
                        throw new InvalidOperationException("Raid evidence destination changed during recovery preparation.");
                    return CompletePreparedEvidence(job, payload, evidence!, nowUtc, storage);
                }
            }
        }
        finally { captured?.Dispose(); }
    }

    private SnapshotPostUploadJobRecord RecoverPreparedUnderMutation(
        SnapshotPostUploadJobRecord job,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (jobs.SnapshotCommitGate)
        {
            // Background scans may hold a stale Prepared record after another request completed it.
            SnapshotPostUploadJobRecord? current = jobs.Find(job.JobId);
            if (current is null) return job;
            job = current;
            if (job.State != SnapshotPostUploadJobState.Prepared || job.ProcessorId != RaidSettlementWork.ProcessorId) return job;
            RaidSettlementDeferredPayload payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
            SaveSnapshotPackage evidencePackage = ReadEvidence(job, payload);
            return CompletePreparedEvidence(job, payload, evidencePackage, nowUtc);
        }
    }
    // Called after evidence validation, while the caller owns the business mutation boundary.
    internal SnapshotPostUploadJobRecord CompletePreparedEvidence(SnapshotPostUploadJobRecord job,
        RaidSettlementDeferredPayload payload, SaveSnapshotPackage evidencePackage,
        DateTimeOffset nowUtc, PreparedSnapshotPayload? storage = null)
    {
        if (snapshots is not IColonySnapshotPackageStore packageStore)
            throw new InvalidOperationException("Raid settlement requires a snapshot package store.");
        SaveSnapshotEnvelope? latest = packageStore.GetLatest(payload.AttackerUserId, payload.AttackerColonyId)?.Envelope;
        if (!SameVersion(latest, evidencePackage.Envelope))
        {
            if (payload.Publication is null || !SameVersion(latest, payload.Publication.Previous))
                throw new InvalidOperationException("Raid evidence destination changed or its original baseline is unknown; recovery cannot overwrite it.");
            if (storage is not null && packageStore is IPreparedColonySnapshotPackageStore preparedStore)
                preparedStore.StorePrepared(evidencePackage, evidencePackage.Index, job.OccurredAtUtc, storage);
            else packageStore.StoreLatest(evidencePackage, evidencePackage.Index, job.OccurredAtUtc);
        }

        players.RecordLatestSnapshotReference(
            payload.AttackerUserId,
            payload.AttackerColonyId,
            payload.EvidenceSnapshotId,
            job.OccurredAtUtc);
        return jobs.MarkReady(job.JobId, nowUtc);
    }

    private SaveSnapshotPackage ReadEvidence(SnapshotPostUploadJobRecord job, RaidSettlementDeferredPayload payload)
    {
        SaveSnapshotPackage evidence = artifacts.Read(payload.EvidenceArtifactId)
            ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found.");
        ValidateBinding(ledger, job, payload, evidence);
        ValidatePayload(evidence);
        return evidence;
    }
}
