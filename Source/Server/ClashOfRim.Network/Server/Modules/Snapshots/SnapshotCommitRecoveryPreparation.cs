using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Capture and install under the owner's snapshot mutation gate; prepare outside it.
internal sealed class SnapshotCommitRecoveryPreparation : ISnapshotRecoveryPreparation
{
    private readonly ISnapshotCommitPreparationOwner owner;
    private readonly SnapshotCommitIntent[] captured;
    private readonly IColonySnapshotIndexStore snapshots;
    private readonly Entry[] entries;
    private bool prepared;

    internal SnapshotCommitRecoveryPreparation(ISnapshotCommitPreparationOwner owner, IColonySnapshotIndexStore snapshots, SnapshotCommitIntent[] captured)
    {
        this.owner = owner;
        this.snapshots = snapshots;
        this.captured = captured;
        entries = captured.Select(intent => new Entry(intent,
            snapshots is IPreparedColonySnapshotPackageStore && intent.PreparedStoragePayload is null && NeedsPublication(intent))).ToArray();
    }

    public bool NeedsPreparation => entries.Any(entry => entry.NeedsPreparation);
    public bool IsCurrent => owner.IsCurrent(captured);

    public void Prepare()
    {
        if (prepared) throw new InvalidOperationException("Snapshot recovery was already prepared.");
        foreach (var entry in entries)
            if (entry.NeedsPreparation)
                entry.Payload = ((IPreparedColonySnapshotPackageStore)snapshots).PreparePayload(entry.Intent.Package.Payload);
        prepared = true;
    }

    public bool TryInstall()
    {
        if (!prepared) throw new InvalidOperationException("Snapshot recovery has not been prepared.");
        if (!IsCurrent) return false;
        // An already-published destination may have rolled back while another intent was being prepared.
        // Recapture the entire batch before allowing recovery to fall back to compression under the gate.
        if (snapshots is IPreparedColonySnapshotPackageStore && entries.Any(entry =>
                entry.Payload is null && entry.Intent.PreparedStoragePayload is null && NeedsPublication(entry.Intent)))
            return false;
        foreach (var entry in entries)
            if (entry.Payload is not null)
                owner.InstallPreparedPayload(entry.Intent, entry.Payload);
        return true;
    }

    private bool NeedsPublication(SnapshotCommitIntent intent)
    {
        var identity = intent.Package.Envelope.Identity;
        return !SnapshotCommitApplication.SameVersion(snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!)?.Envelope, intent.Package.Envelope);
    }

    private sealed class Entry(SnapshotCommitIntent intent, bool needsPreparation)
    {
        public SnapshotCommitIntent Intent { get; } = intent;
        public bool NeedsPreparation { get; } = needsPreparation;
        public PreparedSnapshotPayload? Payload { get; set; }
    }
}
