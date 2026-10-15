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
        using (state.EnterSnapshotMutation())
        {
            ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
            return ScheduleUnderLock(state, request);
        }
    }

    private static SnapshotPostUploadJobRecord ScheduleUnderLock(
        ClashOfRimNetworkState state,
        RaidSettlementScheduleRequest request)
    {
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

        string attackerColonyId = raid.Actor.ColonyId
            ?? throw new InvalidOperationException("Raid attacker colony ID is missing.");
        string defenderColonyId = raid.Target.ColonyId
            ?? throw new InvalidOperationException("Raid defender colony ID is missing.");
        string evidenceSnapshotId = request.Context.Snapshot.Identity.SnapshotId
            ?? throw new InvalidOperationException("Raid settlement evidence snapshot ID is missing.");
        var evidencePackage = new SaveSnapshotPackage(
            request.Context.Snapshot.Envelope,
            request.EvidencePayload,
            request.Context.Snapshot.Index);
        if (request.Context.UserId != raid.Actor.UserId || request.Context.ColonyId != attackerColonyId
            || request.Context.Snapshot.Identity != evidencePackage.Envelope.Identity
            || evidencePackage.Envelope.Identity != new SnapshotIdentity(raid.Actor.UserId, attackerColonyId, evidenceSnapshotId))
            throw new InvalidOperationException("Raid evidence does not belong to the attacker.");
        ValidatePayload(evidencePackage);
        string jobId = "raid-settlement:" + raid.EventId;
        SnapshotPostUploadJobRecord? existing = state.SnapshotPostUploadJobs.Find(jobId);
        if (existing is not null)
        {
            RaidSettlementDeferredPayload original = RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson);
            SaveSnapshotPackage artifact = ReadEvidence(state, existing, original);
            if (original.RaidEventId != raid.EventId || original.DefenderUserId != raid.Target.UserId
                || original.DefenderColonyId != defenderColonyId || original.DefenderSnapshotId != raidPayload.DefenderSnapshotId
                || original.Origin != request.Origin || original.ClientApplicationResult != request.ClientApplicationResult
                || artifact.Envelope != (evidencePackage.Envelope with { NextLineageToken = artifact.Envelope.NextLineageToken }))
                throw new InvalidOperationException("Raid settlement retry differs from the prepared request.");
            return existing.State switch
            {
                SnapshotPostUploadJobState.Prepared => RecoverPrepared(state, existing, request.Context.OccurredAtUtc),
                SnapshotPostUploadJobState.Ready => existing,
                _ => throw new InvalidOperationException(
                    $"Raid settlement '{raid.EventId}' is awaiting manual review: {existing.LastError}")
            };
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
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
            new RaidEvidencePublication(previous, evidencePackage.Envelope));

        // No job references this raid's artifact yet. Replace any orphan from a failed enqueue.
        state.SnapshotPostUploadArtifacts.Delete(artifactId);
        state.SnapshotPostUploadArtifacts.Store(artifactId, evidencePackage);
        SnapshotPostUploadJobRecord prepared = state.SnapshotPostUploadJobs.EnqueuePrepared(
            jobId,
            ProcessorId,
            request.Context,
            payload.Serialize());
        // A store can publish and then throw. Retain both intent and evidence on every failure.
        return RecoverPrepared(state, prepared, request.Context.OccurredAtUtc);
    }

    public static SnapshotPostUploadJobRecord RecoverPrepared(
        ClashOfRimNetworkState state, SnapshotPostUploadJobRecord job, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(job);
        // Online scheduling still owns an outer gate; custom stores retain their original recovery boundary.
        if (Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate)
            || state.SnapshotPostUploadArtifacts is not IVersionedSnapshotPostUploadArtifactStore artifacts)
            return RecoverPreparedUnderLock(state, job, nowUtc);

        RaidSettlementDeferredPayload payload;
        IColonySnapshotPackageStore packages;
        SaveSnapshotEnvelope? latest;
        using (state.EnterSnapshotMutation())
        {
            ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
            lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
            {
                var current = state.SnapshotPostUploadJobs.Find(job.JobId);
                if (current is null) return job;
                job = current;
                if (job.State != SnapshotPostUploadJobState.Prepared || job.ProcessorId != ProcessorId) return job;
                payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
                packages = state.SnapshotStore as IColonySnapshotPackageStore
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
                    captured = artifacts.ReadCaptured(payload.EvidenceArtifactId);
                    evidence = captured.Package ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found.");
                    ValidatePayload(evidence);
                    if (!SameVersion(latest, evidence.Envelope))
                        storage = (packages as IPreparedColonySnapshotPackageStore)?.PreparePayload(evidence.Payload);
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.Xml.XmlException or System.Text.Json.JsonException) { preparationFailure = ex; }
            using (state.EnterSnapshotMutation())
            {
                ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
                lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
                {
                    var current = state.SnapshotPostUploadJobs.Find(job.JobId);
                    if (current is null) return job;
                    if (current != job) return current; // A removed, completed or revised task cannot be revived by old preparation.
                    if (preparationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
                    if (!captured!.IsCurrent) throw new InvalidOperationException("Raid evidence changed during recovery preparation.");
                    ValidateEvidenceBinding(state, job, payload, evidence!);
                    if (packages is IPreparedColonySnapshotPackageStore && storage is null
                        && !SameVersion(packages.GetLatest(payload.AttackerUserId, payload.AttackerColonyId)?.Envelope, evidence!.Envelope))
                        throw new InvalidOperationException("Raid evidence destination changed during recovery preparation.");
                    return CompletePreparedEvidence(state, job, payload, evidence!, nowUtc, storage);
                }
            }
        }
        finally { captured?.Dispose(); }
    }

    private static SnapshotPostUploadJobRecord RecoverPreparedUnderLock(
        ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(job);
        using (state.EnterSnapshotMutation())
        {
            ClashOfRimNetworkServer.RecoverSnapshotCommits(state);
            lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
            {
                // Background scans may hold a stale Prepared record after another request completed it.
                SnapshotPostUploadJobRecord? current = state.SnapshotPostUploadJobs.Find(job.JobId);
                if (current is null) return job;
                job = current;
                if (job.State != SnapshotPostUploadJobState.Prepared || job.ProcessorId != ProcessorId) return job;
                RaidSettlementDeferredPayload payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
                SaveSnapshotPackage evidencePackage = ReadEvidence(state, job, payload);
                return CompletePreparedEvidence(state, job, payload, evidencePackage, nowUtc);
            }
        }
    }

    // Called after evidence validation, while the caller owns the business mutation boundary.
    internal static SnapshotPostUploadJobRecord CompletePreparedEvidence(ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job, RaidSettlementDeferredPayload payload, SaveSnapshotPackage evidencePackage, DateTimeOffset nowUtc,
        PreparedSnapshotPayload? storage = null)
    {
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
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

        state.Players.RecordLatestSnapshotReference(
            payload.AttackerUserId,
            payload.AttackerColonyId,
            payload.EvidenceSnapshotId,
            job.OccurredAtUtc);
        return state.SnapshotPostUploadJobs.MarkReady(job.JobId, nowUtc);
    }

    private static SaveSnapshotPackage ReadEvidence(ClashOfRimNetworkState state,
        SnapshotPostUploadJobRecord job, RaidSettlementDeferredPayload payload)
    {

        SaveSnapshotPackage evidence = state.SnapshotPostUploadArtifacts.Read(payload.EvidenceArtifactId)
            ?? throw new IOException($"Raid settlement evidence artifact '{payload.EvidenceArtifactId}' was not found.");
        ValidateEvidenceBinding(state, job, payload, evidence);
        ValidatePayload(evidence);
        return evidence;
    }

    private static void ValidateEvidenceBinding(ClashOfRimNetworkState state, SnapshotPostUploadJobRecord job,
        RaidSettlementDeferredPayload payload, SaveSnapshotPackage evidence)
    {
        AuthoritativeEvent? raid = state.Ledger.Find(payload.RaidEventId);
        if (raid?.Type != ServerEventType.Raid || raid.Payload is not RaidEventPayload raidPayload
            || raid.Actor.UserId != payload.AttackerUserId || raid.Actor.ColonyId != payload.AttackerColonyId
            || raid.Target.UserId != payload.DefenderUserId || raid.Target.ColonyId != payload.DefenderColonyId
            || raidPayload.DefenderSnapshotId != payload.DefenderSnapshotId
            || job.ProcessorId != ProcessorId || job.Kind != SnapshotPostUploadKind.RaidSettlementEvidence
            || job.JobId != "raid-settlement:" + payload.RaidEventId
            || job.UserId != payload.AttackerUserId || job.ColonyId != payload.AttackerColonyId
            || job.SnapshotId != payload.EvidenceSnapshotId
            || payload.EvidenceArtifactId != BuildArtifactId(payload.RaidEventId, payload.EvidenceSnapshotId)
            || evidence.Envelope.Identity != new SnapshotIdentity(payload.AttackerUserId, payload.AttackerColonyId, payload.EvidenceSnapshotId)
            || (payload.Publication is not null && evidence.Envelope != payload.Publication.Evidence))
            throw new InvalidDataException("Raid evidence artifact does not match its prepared job.");
    }

    internal static void ValidatePayload(SaveSnapshotPackage evidence)
    {
        if (evidence.Payload.LongLength != evidence.Envelope.PayloadBytes
            || !string.Equals(Convert.ToHexString(SHA256.HashData(evidence.Payload)), evidence.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raid evidence payload does not match its envelope.");
    }

    internal static bool SameVersion(SaveSnapshotEnvelope? left, SaveSnapshotEnvelope? right) =>
        left is null || right is null ? left is null && right is null : left.Identity == right.Identity
            && left.OriginalSha256 == right.OriginalSha256 && left.PayloadSha256 == right.PayloadSha256
            && left.NextLineageToken == right.NextLineageToken;

    internal static string BuildArtifactId(string raidEventId, string evidenceSnapshotId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raidEventId + "\u001f" + evidenceSnapshotId));
        return "raid-settlement-" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
