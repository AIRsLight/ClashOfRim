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
    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, MercenaryGuardContractRecord> contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeContractByColony = new(StringComparer.Ordinal);

    public MercenaryGuardContractRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal MercenaryGuardContractRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
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
            MercenaryGuardContractRegistryPersistence? persisted =
                JsonSerializer.Deserialize<MercenaryGuardContractRegistryPersistence>(json, JsonOptions);
            if (persisted?.Contracts is null)
            {
                return;
            }

            foreach (MercenaryGuardContractRecord contract in persisted.Contracts)
            {
                if (string.IsNullOrWhiteSpace(contract.ContractId)
                    || string.IsNullOrWhiteSpace(contract.IdempotencyKey)
                    || string.IsNullOrWhiteSpace(contract.UserId)
                    || string.IsNullOrWhiteSpace(contract.ColonyId))
                {
                    continue;
                }

                contracts[contract.ContractId] = contract;
                idByIdempotencyKey[contract.IdempotencyKey] = contract.ContractId;
                if (string.Equals(contract.Status, StatusActive, StringComparison.Ordinal))
                {
                    activeContractByColony[ColonyKey(contract.UserId, contract.ColonyId)] = contract.ContractId;
                }
            }
        }
        catch (JsonException)
        {
            contracts.Clear();
            idByIdempotencyKey.Clear();
            activeContractByColony.Clear();
        }
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new MercenaryGuardContractRegistryPersistence(contracts.Values
                .OrderBy(contract => contract.CreatedAtUtc)
                .ToList()),
            JsonOptions);
        persistence.Write(json);
    }

    private static string ColonyKey(string userId, string colonyId)
    {
        return userId + "\u001f" + colonyId;
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
