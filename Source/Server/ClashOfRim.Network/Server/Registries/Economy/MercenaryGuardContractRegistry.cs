using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class MercenaryGuardContractRegistry
{
    public const string StatusActive = "Active";
    public const string StatusConsumed = "Consumed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, MercenaryGuardContractRecord> contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeContractByColony = new(StringComparer.Ordinal);

    public MercenaryGuardContractRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal MercenaryGuardContractRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal MercenaryGuardContractRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public MercenaryGuardContractRecord? FindByIdempotencyKey(string idempotencyKey)
    {
        lock (gate)
        {
            return FindByIdempotencyKeyNoLock(idempotencyKey);
        }
    }

    public MercenaryGuardContractRecord? FindActiveForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            return FindActiveForColonyNoLock(userId, colonyId);
        }
    }

    public bool HasActiveForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            return FindActiveForColonyNoLock(userId, colonyId) is not null;
        }
    }

    public MercenaryGuardContractRecord? ActivateWithSnapshot(
        string idempotencyKey,
        string userId,
        string colonyId,
        string contractId,
        string snapshotId,
        string tier,
        int priceSilver,
        float pointRatio,
        long createdAtGameTicks,
        DateTimeOffset createdAtUtc)
    {
        lock (gate)
        {
            MercenaryGuardContractRecord? existing = FindByIdempotencyKeyNoLock(idempotencyKey);
            if (existing is not null)
            {
                return existing;
            }

            if (FindActiveForColonyNoLock(userId, colonyId) is not null)
            {
                return null;
            }

            string resolvedContractId = string.IsNullOrWhiteSpace(contractId)
                ? "mercenary-guard-" + Guid.NewGuid().ToString("N")
                : contractId;
            if (contracts.ContainsKey(resolvedContractId))
            {
                return null;
            }

            var contract = new MercenaryGuardContractRecord(
                resolvedContractId,
                idempotencyKey,
                userId,
                colonyId,
                snapshotId,
                tier,
                priceSilver,
                pointRatio,
                createdAtGameTicks,
                createdAtUtc,
                StatusActive,
                ConsumedRaidEventId: null,
                ConsumedAtUtc: null);
            contracts[contract.ContractId] = contract;
            idByIdempotencyKey[contract.IdempotencyKey] = contract.ContractId;
            activeContractByColony[ColonyKey(userId, colonyId)] = contract.ContractId;
            SaveLocked();
            return contract;
        }
    }

    public MercenaryGuardContractRecord? ConsumeForRaid(
        string userId,
        string colonyId,
        string raidEventId,
        string contractId,
        DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            MercenaryGuardContractRecord? active = FindActiveForColonyNoLock(userId, colonyId);
            if (active is null
                || !string.Equals(active.ContractId, contractId, StringComparison.Ordinal))
            {
                return null;
            }

            MercenaryGuardContractRecord consumed = active with
            {
                Status = StatusConsumed,
                ConsumedRaidEventId = raidEventId,
                ConsumedAtUtc = nowUtc
            };
            contracts[consumed.ContractId] = consumed;
            activeContractByColony.Remove(ColonyKey(userId, colonyId));
            SaveLocked();
            return consumed;
        }
    }

    private MercenaryGuardContractRecord? FindByIdempotencyKeyNoLock(string idempotencyKey)
    {
        return idByIdempotencyKey.TryGetValue(idempotencyKey, out string? contractId)
            && contracts.TryGetValue(contractId, out MercenaryGuardContractRecord? contract)
                ? contract
                : null;
    }

    private MercenaryGuardContractRecord? FindActiveForColonyNoLock(string userId, string colonyId)
    {
        return activeContractByColony.TryGetValue(ColonyKey(userId, colonyId), out string? contractId)
            && contracts.TryGetValue(contractId, out MercenaryGuardContractRecord? contract)
            && string.Equals(contract.Status, StatusActive, StringComparison.Ordinal)
                ? contract
                : null;
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            SaveLocked();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadAll())
        {
            try
            {
                MercenaryGuardContractRecord? contract =
                    JsonSerializer.Deserialize<MercenaryGuardContractRecord>(pair.Value, JsonOptions);
                if (contract is not null)
                {
                    RegisterLoadedContract(contract, overwrite: true);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private bool LoadLegacyReadOnly()
    {
        if (legacyPersistence is null)
        {
            return false;
        }

        string? json = legacyPersistence.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            MercenaryGuardContractRegistryPersistence? persisted =
                JsonSerializer.Deserialize<MercenaryGuardContractRegistryPersistence>(json, JsonOptions);
            if (persisted?.Contracts is null)
            {
                return false;
            }

            bool imported = false;
            foreach (MercenaryGuardContractRecord contract in persisted.Contracts)
            {
                imported |= RegisterLoadedContract(contract, overwrite: false);
            }

            return imported;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void SaveLocked()
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAll(contracts.ToDictionary(
                pair => ContractRowKey(pair.Key),
                pair => JsonSerializer.Serialize(pair.Value, JsonOptions),
                StringComparer.Ordinal));
            return;
        }

        if (legacyPersistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new MercenaryGuardContractRegistryPersistence(contracts.Values
                .OrderBy(contract => contract.CreatedAtUtc)
                .ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private static string ColonyKey(string userId, string colonyId)
    {
        return userId + "\u001f" + colonyId;
    }

    private bool RegisterLoadedContract(MercenaryGuardContractRecord contract, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(contract.ContractId)
            || string.IsNullOrWhiteSpace(contract.IdempotencyKey)
            || string.IsNullOrWhiteSpace(contract.UserId)
            || string.IsNullOrWhiteSpace(contract.ColonyId))
        {
            return false;
        }

        if (contracts.TryGetValue(contract.ContractId, out MercenaryGuardContractRecord? existing))
        {
            if (!overwrite)
            {
                return false;
            }

            if (string.Equals(existing.Status, StatusActive, StringComparison.Ordinal))
            {
                activeContractByColony.Remove(ColonyKey(existing.UserId, existing.ColonyId));
            }
        }

        contracts[contract.ContractId] = contract;
        idByIdempotencyKey[contract.IdempotencyKey] = contract.ContractId;
        if (string.Equals(contract.Status, StatusActive, StringComparison.Ordinal))
        {
            activeContractByColony[ColonyKey(contract.UserId, contract.ColonyId)] = contract.ContractId;
        }

        return true;
    }

    private static string ContractRowKey(string contractId)
    {
        return "contract:" + contractId;
    }

    private sealed record MercenaryGuardContractRegistryPersistence(IReadOnlyList<MercenaryGuardContractRecord> Contracts);
}

public sealed record MercenaryGuardContractRecord(
    string ContractId,
    string IdempotencyKey,
    string UserId,
    string ColonyId,
    string SnapshotId,
    string Tier,
    int PriceSilver,
    float PointRatio,
    long CreatedAtGameTicks,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string? ConsumedRaidEventId,
    DateTimeOffset? ConsumedAtUtc);
