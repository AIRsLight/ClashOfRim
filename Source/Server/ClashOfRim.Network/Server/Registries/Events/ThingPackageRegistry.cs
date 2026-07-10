using System.Text.Json;
using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ThingPackageRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly IThingPackagePersistenceStore? structuredPersistence;
    private readonly Dictionary<string, StoredThingPackageRecord> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByFingerprint = new(StringComparer.Ordinal);

    public ThingPackageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal ThingPackageRegistry(IJsonPersistenceSlot? persistence)
    {
        legacyPersistence = persistence;
        Load();
    }

    internal ThingPackageRegistry(
        IThingPackagePersistenceStore structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public StoredThingPackageRecord Store(
        string idempotencyKey,
        string ownerUserId,
        string? ownerColonyId,
        string? sourceSnapshotId,
        ThingStatePackage package,
        DateTimeOffset nowUtc)
    {
        if (idByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingId)
            && packages.TryGetValue(existingId, out StoredThingPackageRecord? existing))
        {
            return existing;
        }

        string fingerprint = SafeThingStatePackageSerializer.ComputePackageFingerprint(package);
        if (idByFingerprint.TryGetValue(fingerprint, out string? duplicateId)
            && packages.TryGetValue(duplicateId, out StoredThingPackageRecord? duplicate))
        {
            idByIdempotencyKey[idempotencyKey] = duplicate.PackageId;
            PersistIdempotencyKey(idempotencyKey, duplicate.PackageId);
            return duplicate;
        }

        string json = SafeThingStatePackageSerializer.Serialize(package with { Fingerprint = fingerprint });
        var record = new StoredThingPackageRecord(
            "thingpkg-" + Guid.NewGuid().ToString("N"),
            idempotencyKey,
            ownerUserId,
            ownerColonyId,
            sourceSnapshotId,
            nowUtc,
            package.GlobalKey,
            package.DefName,
            package.Label,
            fingerprint,
            json);
        packages[record.PackageId] = record;
        idByIdempotencyKey[idempotencyKey] = record.PackageId;
        idByFingerprint[fingerprint] = record.PackageId;
        Persist(record);
        return record;
    }

    public bool TryGetPackage(string packageId, out ThingStatePackage? package, out string message)
    {
        package = null;
        if (string.IsNullOrWhiteSpace(packageId) || !packages.TryGetValue(packageId, out StoredThingPackageRecord? record))
        {
            message = "thing package not found";
            return false;
        }

        ThingStatePackageReadResult read = SafeThingStatePackageSerializer.Deserialize(record.PackageJson);
        if (!read.Accepted || read.Package is null)
        {
            message = read.Error ?? "thing package parse failed";
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

        foreach (StoredThingPackageRecord record in structuredPersistence.ReadAll())
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
            ThingPackageRegistryPersistence? persisted =
                JsonSerializer.Deserialize<ThingPackageRegistryPersistence>(json, JsonOptions);
            if (persisted?.Packages is null)
            {
                return;
            }

            foreach (StoredThingPackageRecord record in persisted.Packages)
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
            idByFingerprint.Clear();
        }
    }

    private void AddLoadedRecord(StoredThingPackageRecord record, bool persistStructured)
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

        if (!string.IsNullOrWhiteSpace(record.Fingerprint)
            && !idByFingerprint.ContainsKey(record.Fingerprint)
            && packages.ContainsKey(record.PackageId))
        {
            idByFingerprint[record.Fingerprint] = record.PackageId;
        }

        if (missingRecord && persistStructured)
        {
            structuredPersistence?.Upsert(record);
        }
    }

    private void Persist(StoredThingPackageRecord record)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.Upsert(record);
            return;
        }

        SaveLegacy();
    }

    private void PersistIdempotencyKey(string idempotencyKey, string packageId)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.MapIdempotencyKey(idempotencyKey, packageId);
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
            new ThingPackageRegistryPersistence(packages.Values.OrderBy(record => record.CreatedAtUtc).ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private sealed record ThingPackageRegistryPersistence(IReadOnlyList<StoredThingPackageRecord> Packages);
}

public sealed record StoredThingPackageRecord(
    string PackageId,
    string IdempotencyKey,
    string OwnerUserId,
    string? OwnerColonyId,
    string? SourceSnapshotId,
    DateTimeOffset CreatedAtUtc,
    string GlobalKey,
    string? DefName,
    string? Label,
    string Fingerprint,
    string PackageJson);
