using System.Text.Json;
using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ThingPackageRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, StoredThingPackageRecord> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByFingerprint = new(StringComparer.Ordinal);

    public ThingPackageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal ThingPackageRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
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
            Save();
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
        Save();
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
        if (persistence is null)
        {
            return;
        }

        string? json = persistence.Read();
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
                if (string.IsNullOrWhiteSpace(record.PackageId)
                    || string.IsNullOrWhiteSpace(record.IdempotencyKey)
                    || string.IsNullOrWhiteSpace(record.PackageJson))
                {
                    continue;
                }

                packages[record.PackageId] = record;
                idByIdempotencyKey[record.IdempotencyKey] = record.PackageId;
                if (!string.IsNullOrWhiteSpace(record.Fingerprint))
                {
                    idByFingerprint[record.Fingerprint] = record.PackageId;
                }
            }
        }
        catch (JsonException)
        {
            packages.Clear();
            idByIdempotencyKey.Clear();
            idByFingerprint.Clear();
        }
    }

    private void Save()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new ThingPackageRegistryPersistence(packages.Values.OrderBy(record => record.CreatedAtUtc).ToList()),
            JsonOptions);
        persistence.Write(json);
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
