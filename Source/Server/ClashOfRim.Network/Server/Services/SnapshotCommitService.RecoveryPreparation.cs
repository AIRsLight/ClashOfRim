using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed partial class SnapshotCommitService
{
    // Capture and install under the owner's snapshot mutation gate; prepare outside it.
    internal RecoveryPreparation CaptureRecoveryPreparation(IColonySnapshotIndexStore snapshots)
        => new(this, snapshots);

    internal sealed class RecoveryPreparation
    {
        private readonly SnapshotCommitService owner;
        private readonly IColonySnapshotIndexStore snapshots;
        private readonly Entry[] entries;
        private bool prepared;

        internal RecoveryPreparation(SnapshotCommitService owner, IColonySnapshotIndexStore snapshots)
        {
            this.owner = owner;
            this.snapshots = snapshots;
            entries = owner.pending.Values.Select(intent => new Entry(intent,
                snapshots is IPreparedColonySnapshotPackageStore && intent.PreparedStoragePayload is null && NeedsPublication(intent))).ToArray();
        }

        public bool NeedsPreparation => entries.Any(entry => entry.NeedsPreparation);
        public bool IsCurrent => !owner.applying && owner.pending.Count == entries.Length
            && entries.All(entry => owner.pending.TryGetValue(entry.Intent.Id, out var current) && ReferenceEquals(current, entry.Intent));

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
                    owner.pending[entry.Intent.Id] = entry.Intent with { PreparedStoragePayload = entry.Payload };
            return true;
        }

        private bool NeedsPublication(SnapshotCommitIntent intent)
        {
            var identity = intent.Package.Envelope.Identity;
            return !SameVersion(snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!)?.Envelope, intent.Package.Envelope);
        }

        private sealed class Entry(SnapshotCommitIntent intent, bool needsPreparation)
        {
            public SnapshotCommitIntent Intent { get; } = intent;
            public bool NeedsPreparation { get; } = needsPreparation;
            public PreparedSnapshotPayload? Payload { get; set; }
        }
    }
}
