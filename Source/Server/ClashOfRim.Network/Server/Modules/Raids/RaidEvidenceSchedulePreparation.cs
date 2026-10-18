using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using static AIRsLight.ClashOfRim.Network.Modules.Raids.RaidEvidenceRules;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// A request-scoped, nonpersistent preparation. Capture/validate/commit require the business and job gates.
internal sealed class RaidEvidenceSchedulePreparation : IDisposable
{
    private readonly RaidEvidenceScheduling owner;
    private readonly IAuthoritativeEventLedger ledger;
    private readonly ISnapshotPostUploadArtifactStore artifacts;
    private readonly SnapshotPostUploadJobRegistry jobs;
    private readonly SnapshotMutationCoordinator mutations;
    private readonly RaidEvidenceSchedule request;
    private readonly AuthoritativeEvent source;
    private readonly IColonySnapshotPackageStore packages;
    private readonly SaveSnapshotEnvelope? previous, defense;
    private readonly SnapshotPostUploadJobRecord? existing;
    private readonly string jobId, artifactId;
    private SaveSnapshotPackage evidence;
    private ISnapshotPostUploadArtifactRead? read;
    private IPreparedSnapshotPostUploadArtifact? write;
    private PreparedSnapshotPayload? storage;
    private RaidSettlementDeferredPayload? payload;
    private bool prepared, consumed;

    public bool IsReplay => existing is not null;
    public SaveSnapshotEnvelope Envelope => evidence.Envelope;
    public DateTimeOffset OccurredAtUtc => existing?.OccurredAtUtc ?? request.Job.OccurredAtUtc;
    public LatestSnapshotRecord Snapshot => new(evidence.Envelope.Identity, evidence.Envelope, evidence.Index, OccurredAtUtc);
    public bool HasSameJob => jobs.Find(jobId) == existing;

    internal RaidEvidenceSchedulePreparation(RaidEvidenceScheduling owner, IAuthoritativeEventLedger ledger,
        IColonySnapshotPackageStore packages, ISnapshotPostUploadArtifactStore artifacts,
        SnapshotPostUploadJobRegistry jobs, SnapshotMutationCoordinator mutations, RaidEvidenceSchedule request)
    {
        this.owner = owner;
        this.ledger = ledger;
        this.packages = packages;
        this.artifacts = artifacts;
        this.jobs = jobs;
        this.mutations = mutations;
        this.request = request;
        source = Source(ledger, request);
        evidence = new(request.Snapshot.Envelope, request.EvidencePayload, request.Snapshot.Index);
        previous = packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope;
        defense = packages.GetLatest(source.Target.UserId, source.Target.ColonyId!)?.Envelope;
        jobId = "raid-settlement:" + source.EventId;
        existing = jobs.Find(jobId);
        if (existing is not null)
        {
            if (existing.State is not (SnapshotPostUploadJobState.Prepared or SnapshotPostUploadJobState.Ready))
                throw new InvalidOperationException("Raid evidence task requires manual review.");
            payload = RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson);
            artifactId = payload.EvidenceArtifactId;
        }
        else
        {
            if (defense?.Identity.SnapshotId != ((RaidEventPayload)source.Payload).DefenderSnapshotId)
                throw new InvalidOperationException("Raid defender baseline changed.");
            artifactId = BuildArtifactId(source.EventId, evidence.Envelope.Identity.SnapshotId!);
            artifacts.Delete(artifactId); // Only an unreferenced orphan can be replaced.
        }
    }

    public void Prepare(PreparedSnapshotPayload? preparedStorage)
    {
        if (mutations.IsEntered) throw new InvalidOperationException("Raid scheduling preparation must run outside the business gate.");
        evidence = evidence with { Payload = evidence.Payload.ToArray() };
        ValidatePayload(evidence);
        storage = preparedStorage;
        if (existing is not null)
        {
            read = ((IVersionedSnapshotPostUploadArtifactStore)artifacts).ReadCaptured(artifactId);
            var original = read.Package ?? throw new IOException("Raid evidence artifact is missing.");
            ValidatePayload(original);
            if (payload!.RequestHash != request.RequestHash || payload.Origin != request.Origin || payload.ClientApplicationResult != request.ClientApplicationResult
                || original.Envelope != (evidence.Envelope with { CreatedAtUtc = original.Envelope.CreatedAtUtc, NextLineageToken = original.Envelope.NextLineageToken })
                || !original.Payload.AsSpan().SequenceEqual(evidence.Payload))
                throw new InvalidOperationException("Raid evidence retry differs from its original request.");
            evidence = original;
        }
        else
        {
            var defender = packages.GetLatestPackage(source.Target.UserId, source.Target.ColonyId!);
            if (defender is null || !SameVersion(defender.Envelope, defense))
                throw new InvalidOperationException("Raid defender baseline changed while reading.");
            write = ((IPreparedSnapshotPostUploadArtifactStore)artifacts).Prepare(artifactId, evidence);
        }
        if (storage is null && !SameVersion(previous, evidence.Envelope))
            storage = (packages as IPreparedColonySnapshotPackageStore)?.PreparePayload(evidence.Payload);
        prepared = true;
    }

    public bool TryValidate()
    {
        if (!prepared || consumed) throw new InvalidOperationException("Raid schedule preparation is unavailable.");
        if (!HasSameJob) return false;
        var current = ledger.Find(source.EventId);
        if (current is null || current.Status != source.Status || !RaidSettlementSnapshotMutation.SameSource(current, source))
            throw new InvalidOperationException("Raid source changed during scheduling preparation.");
        if (existing is not null)
        {
            if (!read!.IsCurrent) return false;
            ValidateBinding(ledger, existing, payload!, evidence);
            var latest = packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope;
            if (existing.State == SnapshotPostUploadJobState.Prepared && !SameVersion(latest, evidence.Envelope)
                && (payload!.Publication is null || !SameVersion(latest, payload.Publication.Previous)))
                throw new InvalidOperationException("Raid evidence destination changed before replay.");
            if (existing.State == SnapshotPostUploadJobState.Prepared && packages is IPreparedColonySnapshotPackageStore
                && storage is null && !SameVersion(latest, evidence.Envelope))
                return false; // Recapture a rolled-back destination and prepare its bytes outside the gate.
        }
        else
        {
            if (!write!.IsCurrent) return false;
            if (!SameVersion(packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope, previous)
                || !SameVersion(packages.GetLatest(source.Target.UserId, source.Target.ColonyId!)?.Envelope, defense))
                throw new InvalidOperationException("Raid scheduling baseline changed.");
        }
        return true;
    }

    public SnapshotPostUploadJobRecord Commit(RaidEvidenceScheduling scheduling, SaveSnapshotEnvelope envelope, DateTimeOffset at)
    {
        if (scheduling != owner || envelope != evidence.Envelope || !TryValidate())
            throw new InvalidOperationException("Raid scheduling preparation became stale.");
        var job = existing;
        if (job is null)
        {
            if (!write!.TryPublish()) throw new InvalidOperationException("Raid artifact preparation became stale.");
            payload = new(source.EventId, source.Actor.UserId, source.Actor.ColonyId!, source.Target.UserId, source.Target.ColonyId!,
                evidence.Envelope.Identity.SnapshotId!, ((RaidEventPayload)source.Payload).DefenderSnapshotId, artifactId,
                request.Origin, request.ClientApplicationResult, new RaidEvidencePublication(previous, evidence.Envelope), request.RequestHash);
            job = jobs.EnqueuePrepared(jobId, RaidSettlementWork.ProcessorId, request.Job with { OccurredAtUtc = at }, payload.Serialize());
        }
        consumed = true;
        return job.State == SnapshotPostUploadJobState.Ready ? job
            : owner.CompletePreparedEvidence(job, payload!, evidence, at, storage);
    }

    public void Dispose()
    {
        try { write?.Dispose(); }
        finally { read?.Dispose(); }
    }
}
