using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>Durable deletion work; all mutation/recovery calls run under the snapshot mutation gate.</summary>
public sealed class ColonyDeletionService
{
    private readonly string? directory;
    private readonly Dictionary<string, ColonyDeletionIntent> pending = new(StringComparer.Ordinal);
    private int pendingCount;
    private bool applying;

    public ColonyDeletionService(string? directory = null)
    {
        this.directory = directory;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var intent in ReadPending(directory)) pending.Add(intent.Id, intent);
        pendingCount = pending.Count;
    }

    internal bool HasPending => Volatile.Read(ref pendingCount) > 0;
    internal bool IsApplying => applying;
    public static void ValidateDirectory(string directory) { foreach (var _ in ReadPending(directory)) { } }

    private static IEnumerable<ColonyDeletionIntent> ReadPending(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var intent = JsonSerializer.Deserialize<ColonyDeletionIntent>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty colony deletion intent: " + path);
            intent.Validate();
            if (Path.GetFileNameWithoutExtension(path) != intent.Id) throw new InvalidDataException("Colony deletion filename mismatch.");
            yield return intent;
        }
    }

    internal T Commit<T>(ColonyDeletionIntent intent, Func<ColonyDeletionIntent, T> apply)
    {
        intent.Validate();
        if (pending.Count != 0 || applying) throw new InvalidOperationException("Recover colony deletions before accepting new work.");
        if (directory is not null)
        {
            string path = Path.Combine(directory, intent.Id + ".json"), temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, intent);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        pending.Add(intent.Id, intent);
        Volatile.Write(ref pendingCount, pending.Count);
        return Apply(intent, apply);
    }

    internal void Recover(Action<ColonyDeletionIntent> apply)
    {
        if (applying) return;
        foreach (var intent in pending.Values.ToArray()) Apply(intent, item => { apply(item); return true; });
    }

    private T Apply<T>(ColonyDeletionIntent intent, Func<ColonyDeletionIntent, T> apply)
    {
        applying = true;
        try
        {
            T result = apply(intent);
            if (directory is not null) File.Delete(Path.Combine(directory, intent.Id + ".json"));
            pending.Remove(intent.Id);
            Volatile.Write(ref pendingCount, pending.Count);
            return result;
        }
        finally { applying = false; }
    }
}

internal sealed record ColonyDeletionIntent(int Version, string Id, string UserId, string ColonyId,
    SaveSnapshotEnvelope? Previous, DateTimeOffset DeletedAtUtc, LedgerBatch Batch,
    IReadOnlyList<AuthoritativeEvent> Sources, IReadOnlyList<string> NotifyUsers, IReadOnlyList<string> WorldNotifyUsers)
{
    internal AuthoritativeEvent Tombstone => Batch.Events.Single();

    internal void Validate()
    {
        if (Version != 1 || !Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(ColonyId)
            || Previous is not null && (Previous.Identity.OwnerId != UserId || Previous.Identity.ColonyId != ColonyId)
            || Batch is null || Batch.Events is null || Batch.StatusChanges is null || Batch.Events.Count != 1 || Batch.ApplicationChanges?.Count > 0
            || Sources is null || NotifyUsers is null || WorldNotifyUsers is null
            || Sources.Count != Batch.StatusChanges.Count || Sources.Select(item => item.EventId).Distinct().Count() != Sources.Count)
            throw new InvalidDataException("Invalid colony deletion intent.");
        if (!NotifyUsers.Contains(UserId) || !WorldNotifyUsers.Contains(UserId)
            || Sources.SelectMany(item => new[] { item.Actor.UserId, item.Target.UserId })
                .Any(user => !string.IsNullOrWhiteSpace(user) && user != "server" && !NotifyUsers.Contains(user)))
            throw new InvalidDataException("Colony deletion notification recipients are incomplete.");
        if (Tombstone.Type != ServerEventType.ServerNotification || Tombstone.Actor != new EventParty(UserId, ColonyId)
            || Tombstone.Target != new EventParty("server") || Tombstone.Status != ServerEventStatus.AppliedToSnapshot
            || Tombstone.IdempotencyKey != ColonyTombstones.Key(UserId, ColonyId)
            || !AuthoritativeEventFactory.IsKnownEventId(ServerEventType.ServerNotification, Tombstone.IdempotencyKey, Tombstone.EventId)
            || Tombstone.CreatedAtUtc != DeletedAtUtc || Tombstone.AppliedAtUtc != DeletedAtUtc
            || Previous is not null && Tombstone.AppliedSnapshotId != Previous.Identity.SnapshotId
            || Tombstone.Payload is not ServerNotificationEventPayload payload
            || payload.NotificationId != ColonyTombstones.NotificationId(UserId, ColonyId)
            || payload.RelatedUserId != UserId || payload.RelatedColonyId != ColonyId)
            throw new InvalidDataException("Colony deletion tombstone mismatch.");
        foreach (var change in Batch.StatusChanges)
            if (!Sources.Any(item => item.EventId == change.EventId && item.Status == change.ExpectedStatus)
                || change.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed)
                || change.ExpectedPayload is not null || change.Payload is not null)
                throw new InvalidDataException("Invalid colony deletion closure.");
    }

    internal void Preflight(IColonySnapshotIndexStore snapshots, IAuthoritativeEventLedger ledger)
    {
        var latest = snapshots.GetLatest(UserId, ColonyId);
        if (latest is not null && (Previous is null || !SnapshotCommitService.SameVersion(latest.Envelope, Previous)))
            throw new InvalidDataException("Colony deletion conflicts with a changed snapshot; refusing to delete it.");
        foreach (var source in Sources)
            if (ledger.Find(source.EventId) is not { } current || !RaidSettlementSnapshotMutation.SameSource(current, source))
                throw new InvalidDataException("Colony deletion source event changed.");
        if (ledger.Find(Tombstone.EventId) is { } existing && (existing.CreatedAtUtc != Tombstone.CreatedAtUtc
            || existing.AppliedAtUtc != Tombstone.AppliedAtUtc || existing.AppliedSnapshotId != Tombstone.AppliedSnapshotId))
            throw new InvalidDataException("Colony deletion tombstone changed.");
        LedgerBatchValidation.Validate(Batch, ledger.Find, ledger.FindByIdempotencyKey);
    }
}
