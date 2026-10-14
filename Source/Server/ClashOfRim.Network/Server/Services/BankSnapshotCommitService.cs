using System.Text.Json;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>A write-ahead intent bridges snapshot files and the bank registry. Call under the snapshot mutation gate.</summary>
public sealed class BankSnapshotCommitService
{
    private readonly string? directory;
    private readonly Dictionary<string, BankSnapshotCommit> pending = new(StringComparer.Ordinal);
    private bool applying;

    public BankSnapshotCommitService(string? directory = null)
    {
        this.directory = directory;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (BankSnapshotCommit intent in ReadPending(directory)) pending.Add(intent.Id, intent);
    }

    public static void ValidateDirectory(string directory)
    {
        foreach (BankSnapshotCommit _ in ReadPending(directory)) { }
    }

    private static IEnumerable<BankSnapshotCommit> ReadPending(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            BankSnapshotCommit intent = JsonSerializer.Deserialize<BankSnapshotCommit>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty bank snapshot commit: " + path);
            if (intent.Version != 1 || Path.GetFileNameWithoutExtension(path) != intent.Id)
                throw new InvalidDataException("Unsupported bank snapshot commit: " + path);
            SnapshotIdentity identity = intent.Package.Envelope.Identity;
            if (intent.Loan is null && intent.Debt is null
                || intent.Loan is { } loan && (loan.UserId != identity.OwnerId || loan.ColonyId != identity.ColonyId || loan.SnapshotId != identity.SnapshotId)
                || intent.Debt is { } debt && (debt.UserId != identity.OwnerId || debt.ColonyId != identity.ColonyId || debt.SnapshotId != identity.SnapshotId))
                throw new InvalidDataException("Bank journal identity mismatch: " + path);
            yield return intent;
        }
    }

    public void Commit(
        IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, SaveSnapshotPackage package,
        LatestSnapshotRecord snapshot, BankLoanRecord? loan, BankDebtRecord? debt,
        Action<LatestSnapshotRecord, string?> postUpload, string? snapshotUploadKind = null)
    {
        if (loan is null && debt is null) throw new ArgumentException("A bank mutation is required.");
        if (pending.Count != 0) throw new InvalidOperationException("Recover pending bank commits before accepting new work.");
        SnapshotIdentity identity = snapshot.Identity;
        LatestSnapshotRecord? previous = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
        var intent = new BankSnapshotCommit(1, Guid.NewGuid().ToString("N"),
            previous?.Identity.SnapshotId, previous?.Envelope.NextLineageToken,
            package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, snapshot.AcceptedAtUtc, loan, debt, snapshotUploadKind);
        Write(intent);
        pending.Add(intent.Id, intent);
        Apply(intent, snapshots, bank, postUpload);
    }

    public void Recover(IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, Action<LatestSnapshotRecord, string?> postUpload)
    {
        if (applying) return; // A post-upload processor may re-enter the snapshot mutation coordinator.
        foreach (BankSnapshotCommit intent in pending.Values.ToArray()) Apply(intent, snapshots, bank, postUpload);
    }

    private void Apply(BankSnapshotCommit intent, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank,
        Action<LatestSnapshotRecord, string?> postUpload)
    {
        applying = true;
        try
        {
            SaveSnapshotPackage package = intent.Package;
            SnapshotIdentity identity = package.Envelope.Identity;
            LatestSnapshotRecord? latest = snapshots.GetLatest(identity.OwnerId!, identity.ColonyId!);
            bool alreadyPublished = latest?.Identity.SnapshotId == identity.SnapshotId
                && latest?.Envelope.NextLineageToken == package.Envelope.NextLineageToken
                && latest?.Envelope.OriginalSha256 == package.Envelope.OriginalSha256;
            bool stillAtParent = latest?.Identity.SnapshotId == intent.PreviousSnapshotId
                && latest?.Envelope.NextLineageToken == intent.PreviousLineageToken;
            if (!stillAtParent && !alreadyPublished)
                throw new InvalidDataException("A pending bank commit conflicts with a newer snapshot; refusing to overwrite it.");
            var snapshot = new LatestSnapshotRecord(identity, package.Envelope, package.Index, intent.AcceptedAtUtc);
            if (!alreadyPublished)
            {
                if (snapshots is IColonySnapshotPackageStore packages) packages.StoreLatest(package, package.Index, intent.AcceptedAtUtc);
                else snapshots.StoreLatest(snapshot);
            }
            bank.ApplySnapshotCommit(intent.Loan, intent.Debt);
            // Processors are idempotent; retain the intent if scheduling downstream work fails.
            postUpload(snapshot, intent.SnapshotUploadKind);
            if (directory is not null) File.Delete(Path.Combine(directory, intent.Id + ".json"));
            pending.Remove(intent.Id);
        }
        finally { applying = false; }
    }

    private void Write(BankSnapshotCommit intent)
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

public sealed record BankSnapshotCommit(int Version, string Id, string? PreviousSnapshotId, string? PreviousLineageToken,
    SaveSnapshotPackage Package, DateTimeOffset AcceptedAtUtc, BankLoanRecord? Loan, BankDebtRecord? Debt, string? SnapshotUploadKind);
