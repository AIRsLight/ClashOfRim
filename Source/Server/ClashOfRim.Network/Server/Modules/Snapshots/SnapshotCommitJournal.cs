using System.Text.Json;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Owns the durable intent format and file lifecycle; the commit coordinator alone owns pending work.
internal sealed class SnapshotCommitJournal
{
    private readonly string? directory;

    internal SnapshotCommitJournal(string? directory)
    {
        this.directory = directory;
        if (directory is not null) Directory.CreateDirectory(directory);
    }

    internal IEnumerable<SnapshotCommitIntent> ReadPending() => ReadPending(directory);

    internal static void ValidateDirectory(string directory)
    {
        foreach (SnapshotCommitIntent _ in ReadPending(directory)) { }
    }

    private static IEnumerable<SnapshotCommitIntent> ReadPending(string? directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            SnapshotCommitIntent intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty snapshot commit: " + path);
            if (intent.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12) || Path.GetFileNameWithoutExtension(path) != intent.Id)
                throw new InvalidDataException("Unsupported snapshot commit: " + path);
            SnapshotIdentity identity = intent.Package.Envelope.Identity;
            if (intent.Loan is null && intent.Debt is null && intent.Gift is null && intent.Trade is null && intent.Shop is null && intent.Mercenary is null && intent.PendingOperations is null && intent.Raid is null && intent.RaidSettlement is null && intent.RaidCleanup is null && intent.Upload is null && intent.Support is null
                || intent.Loan is { } loan && (loan.UserId != identity.OwnerId || loan.ColonyId != identity.ColonyId || loan.SnapshotId != identity.SnapshotId)
                || intent.Debt is { } debt && (debt.UserId != identity.OwnerId || debt.ColonyId != identity.ColonyId || debt.SnapshotId != identity.SnapshotId)
                || intent.Gift is { } gift && (intent.Version is not (2 or 6) || intent.Loan is not null || intent.Debt is not null
                    || !GiftSnapshotReplayPolicy.Matches(gift, identity.OwnerId!, identity.ColonyId, identity.SnapshotId))
                || intent.Trade is not null && (intent.Version is not (3 or 6) || intent.Loan is not null || intent.Debt is not null || intent.Gift is not null)
                || intent.Shop is not null && (intent.Version is not (4 or 6) || intent.Loan is not null || intent.Debt is not null || intent.Gift is not null || intent.Trade is not null)
                || intent.Mercenary is not null && (intent.Version is not (5 or 6) || intent.Loan is not null || intent.Debt is not null || intent.Gift is not null || intent.Trade is not null || intent.Shop is not null))
                throw new InvalidDataException("Snapshot journal identity mismatch: " + path);
            if ((intent.Version == 7) != (intent.Raid is not null)
                || intent.Raid is not null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null
                    || intent.Trade is not null || intent.Shop is not null || intent.Mercenary is not null))
                throw new InvalidDataException("Raid journal contains incompatible primary mutations: " + path);
            intent.Raid?.Validate(identity);
            if ((intent.Version == 8) != (intent.RaidSettlement is not null)
                || intent.RaidSettlement is not null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null
                    || intent.Trade is not null || intent.Shop is not null || intent.Mercenary is not null || intent.Raid is not null || intent.PendingOperations is not null))
                throw new InvalidDataException("Raid settlement journal contains incompatible mutations: " + path);
            intent.RaidSettlement?.Validate(identity);
            if ((intent.Version == 9) != (intent.RaidCleanup is not null)
                || intent.RaidCleanup is not null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null
                    || intent.Trade is not null || intent.Shop is not null || intent.Mercenary is not null || intent.Raid is not null
                    || intent.RaidSettlement is not null || intent.PendingOperations is not null))
                throw new InvalidDataException("Raid cleanup journal contains incompatible mutations: " + path);
            intent.RaidCleanup?.Validate(identity);
            if ((intent.Version is 10 or 11) != (intent.Upload is not null)
                || intent.Upload is not null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null
                    || intent.Trade is not null || intent.Shop is not null || intent.Mercenary is not null || intent.Raid is not null
                    || intent.RaidSettlement is not null || intent.RaidCleanup is not null))
                throw new InvalidDataException("Ordinary upload journal contains incompatible mutations: " + path);
            if ((intent.Version == 12) != (intent.Support is not null)
                || intent.Support is not null && (intent.Loan is not null || intent.Debt is not null || intent.Gift is not null
                    || intent.Trade is not null || intent.Shop is not null || intent.Mercenary is not null || intent.Raid is not null
                    || intent.RaidSettlement is not null || intent.RaidCleanup is not null || intent.Upload is not null))
                throw new InvalidDataException("Support journal contains incompatible mutations: " + path);
            intent.Support?.Validate(identity);
            intent.Upload?.Validate(intent);
            intent.Trade?.Validate(identity);
            intent.Shop?.Validate(identity);
            intent.Mercenary?.Validate(identity);
            if (intent.Version == 6 && intent.PendingOperations is null || intent.Version < 6 && intent.PendingOperations is not null)
                throw new InvalidDataException("Snapshot confirmation journal is missing its operations: " + path);
            if (intent.PendingOperations is { } operations)
            {
                operations.Validate(new LatestSnapshotRecord(identity, intent.Package.Envelope, intent.Package.Index, intent.AcceptedAtUtc),
                    allowEmpty: intent.Upload is not null);
                if (operations.Loan is { } pendingLoan && pendingLoan.LoanId == intent.Loan?.LoanId
                    || operations.Debts.Any(debt => debt.DebtId == intent.Debt?.DebtId)
                    || operations.Mercenaries.Any(contract => contract.ContractId == intent.Mercenary?.Contract?.ContractId))
                    throw new InvalidDataException("A primary mutation overlaps its confirmation batch: " + path);
            }
            yield return intent;
        }
    }

    internal void Write(SnapshotCommitIntent intent, bool replace = false)
    {
        intent.PreparedStoragePayload?.Validate(intent.Package.Payload);
        if (directory is null) return;
        string path = Path.Combine(directory, intent.Id + ".json");
        SnapshotJournalFile.Write(path, intent, replace);
    }

    internal void Remove(string id)
    {
        if (directory is not null) File.Delete(Path.Combine(directory, id + ".json"));
    }
}
