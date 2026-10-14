using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>A write-ahead intent bridges snapshot files and business records. Call under the snapshot mutation gate.</summary>
public sealed class SnapshotCommitService
{
    private readonly string? directory;
    private readonly Dictionary<string, SnapshotCommitIntent> pending = new(StringComparer.Ordinal);
    private bool applying;

    public SnapshotCommitService(string? directory = null)
    {
        this.directory = directory;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (SnapshotCommitIntent intent in ReadPending(directory)) pending.Add(intent.Id, intent);
    }

    public static void ValidateDirectory(string directory)
    {
        foreach (SnapshotCommitIntent _ in ReadPending(directory)) { }
    }

    private static IEnumerable<SnapshotCommitIntent> ReadPending(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            SnapshotCommitIntent intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty snapshot commit: " + path);
            if (intent.Version is not (1 or 2 or 3) || Path.GetFileNameWithoutExtension(path) != intent.Id)
                throw new InvalidDataException("Unsupported snapshot commit: " + path);
            SnapshotIdentity identity = intent.Package.Envelope.Identity;
            if (intent.Loan is null && intent.Debt is null && intent.Gift is null && intent.Trade is null
                || intent.Loan is { } loan && (loan.UserId != identity.OwnerId || loan.ColonyId != identity.ColonyId || loan.SnapshotId != identity.SnapshotId)
                || intent.Debt is { } debt && (debt.UserId != identity.OwnerId || debt.ColonyId != identity.ColonyId || debt.SnapshotId != identity.SnapshotId)
                || intent.Gift is { } gift && (intent.Version != 2 || intent.Loan is not null || intent.Debt is not null
                    || !GiftSnapshotReplayPolicy.Matches(gift, identity.OwnerId!, identity.ColonyId, identity.SnapshotId))
                || intent.Trade is not null && (intent.Version != 3 || intent.Loan is not null || intent.Debt is not null || intent.Gift is not null))
                throw new InvalidDataException("Snapshot journal identity mismatch: " + path);
            intent.Trade?.Validate(identity);
            yield return intent;
        }
    }

    public void Commit(
        IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, SaveSnapshotPackage package,
        LatestSnapshotRecord snapshot, BankLoanRecord? loan, BankDebtRecord? debt,
        Action<LatestSnapshotRecord, string?> postUpload, string? snapshotUploadKind = null)
    {
        if (loan is null && debt is null) throw new ArgumentException("A bank mutation is required.");
        if (pending.Count != 0) throw new InvalidOperationException("Recover pending snapshot commits before accepting new work.");
        SnapshotIdentity identity = snapshot.Identity;
        LatestSnapshotRecord? previous = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        var intent = new SnapshotCommitIntent(1, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc, loan, debt, snapshotUploadKind);
        Write(intent);
        pending.Add(intent.Id, intent);
        Apply(intent, snapshots, bank, postUpload);
    }

    public void Recover(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, Action<LatestSnapshotRecord, string?> postUpload)
        => Recover(snapshots, bank, postUpload, null);

    public void Recover(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger,
        Action<string>? signalUser = null, Action<AuthoritativeEvent>? recordTrade = null)
    {
        if (applying) return; // A post-upload processor may re-enter the snapshot mutation coordinator.
        foreach (SnapshotCommitIntent intent in pending.Values.ToArray()) Apply(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade);
    }

    public void CommitTrade(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        IAuthoritativeEventLedger ledger, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        TradeSnapshotMutation trade, Action<LatestSnapshotRecord, string?> postUpload, Action<string> signalUser,
        Action<AuthoritativeEvent> recordTrade, string? snapshotUploadKind = null)
    {
        if (pending.Count != 0) throw new InvalidOperationException("Recover pending snapshot commits before accepting new work.");
        trade.Validate(snapshot.Identity);
        LedgerBatchValidation.Validate(trade.Batch, ledger.Find, ledger.FindByIdempotencyKey);
        LatestSnapshotRecord? previous = snapshots.GetLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
        var intent = new SnapshotCommitIntent(3, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
            null, null, snapshotUploadKind, Trade: trade);
        Write(intent);
        pending.Add(intent.Id, intent);
        Apply(intent, snapshots, bank, postUpload, ledger, signalUser, recordTrade);
    }

    public void CommitGift(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        IAuthoritativeEventLedger ledger, SaveSnapshotPackage package, LatestSnapshotRecord snapshot,
        AuthoritativeEvent gift, Action<LatestSnapshotRecord, string?> postUpload, Action<string> signalUser,
        string? snapshotUploadKind = null)
    {
        if (pending.Count != 0) throw new InvalidOperationException("Recover pending snapshot commits before accepting new work.");
        SnapshotIdentity identity = snapshot.Identity;
        if (!GiftSnapshotReplayPolicy.Matches(gift, identity.OwnerId!, identity.ColonyId, identity.SnapshotId))
            throw new ArgumentException("Gift must belong to the confirmed snapshot.");
        if (ledger.FindByIdempotencyKey(gift.IdempotencyKey) is not null)
            throw new InvalidOperationException("Gift idempotency key is already in use.");
        LatestSnapshotRecord? previous = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        var intent = new SnapshotCommitIntent(2, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc,
            null, null, snapshotUploadKind, gift);
        Write(intent);
        pending.Add(intent.Id, intent);
        Apply(intent, snapshots, bank, postUpload, ledger, signalUser);
    }

    private void Apply(SnapshotCommitIntent intent, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload, IAuthoritativeEventLedger? ledger = null,
        Action<string>? signalUser = null, Action<AuthoritativeEvent>? recordTrade = null)
    {
        applying = true;
        try
        {
            SaveSnapshotPackage package = intent.Package;
            SnapshotIdentity identity = package.Envelope.Identity;
            if (intent.Trade is { } pendingTrade)
            {
                if (ledger is null) throw new InvalidOperationException("Trade recovery requires the event ledger.");
                LedgerBatchValidation.Validate(pendingTrade.Batch, ledger.Find, ledger.FindByIdempotencyKey);
            }
            if (intent.Gift is { } pendingGift)
            {
                if (ledger is null) throw new InvalidOperationException("Gift recovery requires the event ledger.");
                AuthoritativeEvent? existing = ledger.FindByIdempotencyKey(pendingGift.IdempotencyKey);
                if (existing is not null && existing.EventId != pendingGift.EventId)
                    throw new InvalidDataException("Gift journal conflicts with an existing event.");
            }
            LatestSnapshotRecord? latest = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
            bool alreadyPublished = latest?.Identity.SnapshotId == identity.SnapshotId
                && latest?.Envelope.NextLineageToken == package.Envelope.NextLineageToken
                && latest?.Envelope.OriginalSha256 == package.Envelope.OriginalSha256;
            bool stillAtParent = latest?.Identity.SnapshotId == intent.PreviousSnapshotId
                && latest?.Envelope.NextLineageToken == intent.PreviousLineageToken;
            if (!stillAtParent && !alreadyPublished)
                throw new InvalidDataException("A pending snapshot commit conflicts with a newer snapshot; refusing to overwrite it.");
            var snapshot = new LatestSnapshotRecord(identity, package.Envelope, package.Index, intent.AcceptedAtUtc);
            if (!alreadyPublished)
            {
                if (snapshots is IColonySnapshotPackageStore packages) packages.StoreLatest(package, package.Index, intent.AcceptedAtUtc);
                else snapshots.StoreLatest(snapshot);
            }
            if (intent.Gift is { } gift)
            {
                LedgerAppendResult append = ledger!.Append(gift);
                if (append.Event.EventId != gift.EventId)
                    throw new InvalidDataException("Gift idempotency key was claimed by another event.");
                // Repeat the wake-up on recovery even if Append already succeeded before interruption.
                signalUser?.Invoke(gift.Target.UserId);
            }
            else if (intent.Trade is { } trade)
            {
                ledger!.ApplyBatch(trade.Batch);
                recordTrade?.Invoke(trade.Batch.Events[0]);
                foreach (string userId in trade.NotifyUsers) signalUser?.Invoke(userId);
            }
            else bank.ApplySnapshotCommit(intent.Loan, intent.Debt);
            // Processors are idempotent; retain the intent if scheduling downstream work fails.
            postUpload(snapshot, intent.SnapshotUploadKind);
            if (directory is not null) File.Delete(Path.Combine(directory, intent.Id + ".json"));
            pending.Remove(intent.Id);
        }
        finally { applying = false; }
    }

    private void Write(SnapshotCommitIntent intent)
    {
        if (directory is null) return;
        string path = Path.Combine(directory, intent.Id + ".json");
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, intent);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }
}

public sealed record SnapshotCommitIntent(int Version, string Id, string? PreviousSnapshotId, string? PreviousLineageToken,
    SaveSnapshotPackage Package, DateTimeOffset AcceptedAtUtc, BankLoanRecord? Loan, BankDebtRecord? Debt, string? SnapshotUploadKind,
    AuthoritativeEvent? Gift = null, TradeSnapshotMutation? Trade = null);
