using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public sealed class PawnPackageRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly IPawnPackagePersistenceStore? structuredPersistence;
    private readonly Dictionary<string, StoredPawnPackageRecord> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);

    public PawnPackageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal PawnPackageRegistry(IJsonPersistenceSlot? persistence)
    {
        legacyPersistence = persistence;
        Load();
    }

    internal PawnPackageRegistry(
        IPawnPackagePersistenceStore structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public StoredPawnPackageRecord Store(
        string idempotencyKey,
        string ownerUserId,
        string? ownerColonyId,
        string? sourceSnapshotId,
        PawnExchangePackage package,
        DateTimeOffset nowUtc)
    {
        if (idByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingId)
            && packages.TryGetValue(existingId, out StoredPawnPackageRecord? existing))
        {
            return existing;
        }

        string json = SafePawnExchangeSerializer.Serialize(package);
        var record = new StoredPawnPackageRecord(
            "pawnpkg-" + Guid.NewGuid().ToString("N"),
            idempotencyKey,
            ownerUserId,
            ownerColonyId,
            sourceSnapshotId,
            nowUtc,
            package.Reference.GlobalId,
            package.Appearance.DisplayName,
            package.Identity.ThingDef,
            json);
        packages[record.PackageId] = record;
        idByIdempotencyKey[idempotencyKey] = record.PackageId;
        Persist(record);
        return record;
    }

    public bool TryGetPackage(string packageId, out PawnExchangePackage? package, out string message)
    {
        package = null;
        if (string.IsNullOrWhiteSpace(packageId) || !packages.TryGetValue(packageId, out StoredPawnPackageRecord? record))
        {
            message = ServerLocalization.Text("PawnPackage.NotFound");
            return false;
        }

        PawnExchangeReadResult read = SafePawnExchangeSerializer.Deserialize(record.PackageJson);
        if (!read.Accepted || read.Package is null)
        {
            message = read.Error ?? ServerLocalization.Text("PawnPackage.ParseFailed");
            return false;
        }

        package = read.Package;
        message = string.Empty;
        return true;
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        if (!hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive))
        {
            LoadLegacyReadOnly();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (StoredPawnPackageRecord record in structuredPersistence.ReadAll())
        {
            AddLoadedRecord(record, persistStructured: false);
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadIdempotencyMap())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key)
                && !string.IsNullOrWhiteSpace(pair.Value)
                && packages.ContainsKey(pair.Value))
            {
                idByIdempotencyKey[pair.Key] = pair.Value;
            }
        }
    }

    private void LoadLegacyReadOnly()
    {
        if (legacyPersistence is null)
        {
            return;
        }

        string? json = legacyPersistence.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            PawnPackageRegistryPersistence? persisted =
                JsonSerializer.Deserialize<PawnPackageRegistryPersistence>(json, JsonOptions);
            if (persisted?.Packages is null)
            {
                return;
            }

            foreach (StoredPawnPackageRecord record in persisted.Packages)
            {
                AddLoadedRecord(
                    record,
                    persistStructured: structuredPersistence is not null && LegacyStructuredImportScope.IsActive);
            }
        }
        catch (JsonException)
        {
            packages.Clear();
            idByIdempotencyKey.Clear();
        }
    }

    private void AddLoadedRecord(StoredPawnPackageRecord record, bool persistStructured)
    {
        if (string.IsNullOrWhiteSpace(record.PackageId)
            || string.IsNullOrWhiteSpace(record.IdempotencyKey)
            || string.IsNullOrWhiteSpace(record.PackageJson))
        {
            return;
        }

        bool missingRecord = !packages.ContainsKey(record.PackageId);
        if (missingRecord)
        {
            packages[record.PackageId] = record;
        }

        if (!idByIdempotencyKey.ContainsKey(record.IdempotencyKey)
            && packages.ContainsKey(record.PackageId))
        {
            idByIdempotencyKey[record.IdempotencyKey] = record.PackageId;
            if (persistStructured)
            {
                structuredPersistence?.MapIdempotencyKey(record.IdempotencyKey, record.PackageId);
            }
        }

        if (missingRecord && persistStructured)
        {
            structuredPersistence?.Upsert(record);
        }
    }

    private void Persist(StoredPawnPackageRecord record)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.Upsert(record);
            return;
        }

        SaveLegacy();
    }

    private void SaveLegacy()
    {
        if (legacyPersistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new PawnPackageRegistryPersistence(packages.Values.OrderBy(record => record.CreatedAtUtc).ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private sealed record PawnPackageRegistryPersistence(IReadOnlyList<StoredPawnPackageRecord> Packages);
}

public sealed record StoredPawnPackageRecord(
    string PackageId,
    string IdempotencyKey,
    string OwnerUserId,
    string? OwnerColonyId,
    string? SourceSnapshotId,
    DateTimeOffset CreatedAtUtc,
    string PawnGlobalId,
    string? PawnName,
    string? ThingDef,
    string PackageJson);
