using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

// A request-scoped, nonpersistent preparation. Capture/validate/commit require the business and job gates.
internal sealed class RaidEvidenceSchedulePreparation : IDisposable
{
    private readonly ClashOfRimNetworkState state;
    private readonly RaidSettlementScheduleRequest request;
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

    public static bool Supports(ClashOfRimNetworkState state) => state.SnapshotStore is IColonySnapshotPackageStore
        && state.SnapshotPostUploadArtifacts is IPreparedSnapshotPostUploadArtifactStore and IVersionedSnapshotPostUploadArtifactStore;
    public bool IsReplay => existing is not null;
    public SaveSnapshotEnvelope Envelope => evidence.Envelope;
    public SnapshotPostUploadContext Context => request.Context with
    {
        Snapshot = new LatestSnapshotRecord(evidence.Envelope.Identity, evidence.Envelope, evidence.Index, existing?.OccurredAtUtc ?? request.Context.OccurredAtUtc),
        OccurredAtUtc = existing?.OccurredAtUtc ?? request.Context.OccurredAtUtc
    };
    public bool HasSameJob => state.SnapshotPostUploadJobs.Find(jobId) == existing;

    public RaidEvidenceSchedulePreparation(ClashOfRimNetworkState state, RaidSettlementScheduleRequest request)
    {
        this.state = state;
        this.request = request;
        source = state.Ledger.Find(request.RaidEventId) ?? throw new InvalidOperationException("Raid source disappeared.");
        if (source.Type != ServerEventType.Raid || source.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player, RequiresSettlement: true }
            || source.Actor.UserId != request.Context.UserId || source.Actor.ColonyId != request.Context.ColonyId
            || request.Context.Snapshot.Envelope.Identity.OwnerId != source.Actor.UserId
            || request.Context.Snapshot.Envelope.Identity.ColonyId != source.Actor.ColonyId
            || request.Context.Kind != SnapshotPostUploadKind.RaidSettlementEvidence
            || request.Context.Snapshot.Identity != request.Context.Snapshot.Envelope.Identity)
            throw new InvalidOperationException("Raid evidence does not match its source.");
        packages = (IColonySnapshotPackageStore)state.SnapshotStore;
        evidence = new(request.Context.Snapshot.Envelope, request.EvidencePayload, request.Context.Snapshot.Index);
        previous = packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope;
        defense = packages.GetLatest(source.Target.UserId, source.Target.ColonyId!)?.Envelope;
        jobId = "raid-settlement:" + source.EventId;
        existing = state.SnapshotPostUploadJobs.Find(jobId);
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
            artifactId = RaidSettlementDeferredScheduler.BuildArtifactId(source.EventId, evidence.Envelope.Identity.SnapshotId!);
            state.SnapshotPostUploadArtifacts.Delete(artifactId); // Only an unreferenced orphan can be replaced.
        }
    }

    public void Prepare(PreparedSnapshotPayload? preparedStorage)
    {
        if (Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate)) throw new InvalidOperationException("Raid scheduling preparation must run outside the business gate.");
        evidence = evidence with { Payload = evidence.Payload.ToArray() };
        RaidSettlementDeferredScheduler.ValidatePayload(evidence);
        storage = preparedStorage;
        if (existing is not null)
        {
            read = ((IVersionedSnapshotPostUploadArtifactStore)state.SnapshotPostUploadArtifacts).ReadCaptured(artifactId);
            var original = read.Package ?? throw new IOException("Raid evidence artifact is missing.");
            RaidSettlementDeferredScheduler.ValidatePayload(original);
            if (payload!.RequestHash != request.RequestHash || payload.Origin != request.Origin || payload.ClientApplicationResult != request.ClientApplicationResult
                || original.Envelope != (evidence.Envelope with { CreatedAtUtc = original.Envelope.CreatedAtUtc, NextLineageToken = original.Envelope.NextLineageToken })
                || !original.Payload.AsSpan().SequenceEqual(evidence.Payload))
                throw new InvalidOperationException("Raid evidence retry differs from its original request.");
            evidence = original;
        }
        else
        {
            var defender = packages.GetLatestPackage(source.Target.UserId, source.Target.ColonyId!);
            if (defender is null || !RaidSettlementDeferredScheduler.SameVersion(defender.Envelope, defense))
                throw new InvalidOperationException("Raid defender baseline changed while reading.");
            write = ((IPreparedSnapshotPostUploadArtifactStore)state.SnapshotPostUploadArtifacts).Prepare(artifactId, evidence);
        }
        if (storage is null && !RaidSettlementDeferredScheduler.SameVersion(previous, evidence.Envelope))
            storage = (packages as IPreparedColonySnapshotPackageStore)?.PreparePayload(evidence.Payload);
        prepared = true;
    }

    public bool TryValidate()
    {
        if (!prepared || consumed) throw new InvalidOperationException("Raid schedule preparation is unavailable.");
        if (!HasSameJob) return false;
        var current = state.Ledger.Find(source.EventId);
        if (current is null || current.Status != source.Status || !RaidSettlementSnapshotMutation.SameSource(current, source))
            throw new InvalidOperationException("Raid source changed during scheduling preparation.");
        if (existing is not null)
        {
            if (!read!.IsCurrent) return false;
            RaidSettlementDeferredScheduler.ValidateEvidenceBinding(state, existing, payload!, evidence);
            var latest = packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope;
            if (existing.State == SnapshotPostUploadJobState.Prepared && !RaidSettlementDeferredScheduler.SameVersion(latest, evidence.Envelope)
                && (payload!.Publication is null || !RaidSettlementDeferredScheduler.SameVersion(latest, payload.Publication.Previous)))
                throw new InvalidOperationException("Raid evidence destination changed before replay.");
            if (existing.State == SnapshotPostUploadJobState.Prepared && packages is IPreparedColonySnapshotPackageStore
                && storage is null && !RaidSettlementDeferredScheduler.SameVersion(latest, evidence.Envelope))
                return false; // Recapture a rolled-back destination and prepare its bytes outside the gate.
        }
        else
        {
            if (!write!.IsCurrent) return false;
            if (!RaidSettlementDeferredScheduler.SameVersion(packages.GetLatest(source.Actor.UserId, source.Actor.ColonyId!)?.Envelope, previous)
                || !RaidSettlementDeferredScheduler.SameVersion(packages.GetLatest(source.Target.UserId, source.Target.ColonyId!)?.Envelope, defense))
                throw new InvalidOperationException("Raid scheduling baseline changed.");
        }
        return true;
    }

    public SnapshotPostUploadJobRecord Commit(SnapshotPostUploadContext context)
    {
        if (context.State != state || context.Snapshot.Envelope != evidence.Envelope || !TryValidate())
            throw new InvalidOperationException("Raid scheduling preparation became stale.");
        var job = existing;
        if (job is null)
        {
            if (!write!.TryPublish()) throw new InvalidOperationException("Raid artifact preparation became stale.");
            payload = new(source.EventId, source.Actor.UserId, source.Actor.ColonyId!, source.Target.UserId, source.Target.ColonyId!,
                evidence.Envelope.Identity.SnapshotId!, ((RaidEventPayload)source.Payload).DefenderSnapshotId, artifactId,
                request.Origin, request.ClientApplicationResult, new RaidEvidencePublication(previous, evidence.Envelope), request.RequestHash);
            job = state.SnapshotPostUploadJobs.EnqueuePrepared(jobId, RaidSettlementDeferredScheduler.ProcessorId, context, payload.Serialize());
        }
        consumed = true;
        return job.State == SnapshotPostUploadJobState.Ready ? job
            : RaidSettlementDeferredScheduler.CompletePreparedEvidence(state, job, payload!, evidence, context.OccurredAtUtc, storage);
    }

    public void Dispose()
    {
        try { write?.Dispose(); }
        finally { read?.Dispose(); }
    }
}
