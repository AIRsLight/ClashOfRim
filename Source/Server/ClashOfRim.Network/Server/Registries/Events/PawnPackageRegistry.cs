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

    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, StoredPawnPackageRecord> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);

    public PawnPackageRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal PawnPackageRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
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
        Save();
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
            PawnPackageRegistryPersistence? persisted =
                JsonSerializer.Deserialize<PawnPackageRegistryPersistence>(json, JsonOptions);
            if (persisted?.Packages is null)
            {
                return;
            }

            foreach (StoredPawnPackageRecord record in persisted.Packages)
            {
                if (string.IsNullOrWhiteSpace(record.PackageId)
                    || string.IsNullOrWhiteSpace(record.IdempotencyKey)
                    || string.IsNullOrWhiteSpace(record.PackageJson))
                {
                    continue;
                }

                packages[record.PackageId] = record;
                idByIdempotencyKey[record.IdempotencyKey] = record.PackageId;
            }
        }
        catch (JsonException)
        {
            packages.Clear();
            idByIdempotencyKey.Clear();
        }
    }

    private void Save()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new PawnPackageRegistryPersistence(packages.Values.OrderBy(record => record.CreatedAtUtc).ToList()),
            JsonOptions);
        persistence.Write(json);
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
