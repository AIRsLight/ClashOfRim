using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class MercenaryContractRegistry
{
    public const string StatusPendingActivation = "PendingActivation";
    public const string StatusActive = "Active";
    public const string StatusCompleted = "Completed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, MercenaryContractRecord> contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> openContractCountByColony = new(StringComparer.Ordinal);

    public MercenaryContractRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal MercenaryContractRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal MercenaryContractRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public MercenaryContractRecord? FindByIdempotencyKey(string idempotencyKey)
    {
        lock (gate)
        {
            return idByIdempotencyKey.TryGetValue(idempotencyKey, out string? contractId)
                && contracts.TryGetValue(contractId, out MercenaryContractRecord? contract)
                    ? contract
                    : null;
        }
    }

    public MercenaryContractRecord? Find(string contractId)
    {
        lock (gate)
        {
            return contracts.TryGetValue(contractId, out MercenaryContractRecord? contract)
                ? contract
                : null;
        }
    }

    public MercenaryContractRecord? FindActive(string contractId)
    {
        lock (gate)
        {
            return contracts.TryGetValue(contractId, out MercenaryContractRecord? contract)
                && string.Equals(contract.Status, StatusActive, StringComparison.Ordinal)
                    ? contract
                    : null;
        }
    }

    public int CountOpenForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            return openContractCountByColony.TryGetValue(ColonyKey(userId, colonyId), out int count)
                ? count
                : 0;
        }
    }

    public int CountActiveForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            return contracts.Values.Count(contract =>
                string.Equals(contract.UserId, userId, StringComparison.Ordinal)
                && string.Equals(contract.ColonyId, colonyId, StringComparison.Ordinal)
                && string.Equals(contract.Status, StatusActive, StringComparison.Ordinal));
        }
    }

    public MercenaryContractRecord Create(
        string idempotencyKey,
        string userId,
        string colonyId,
        string snapshotId,
        string skillDefName,
        int skillLevel,
        int durationDays,
        int priceSilver,
        int harmfulSurgeryFineSilver,
        int deathFineSilver,
        long createdAtGameTicks,
        DateTimeOffset createdAtUtc,
        string? requestedContractId = null)
    {
        lock (gate)
        {
            MercenaryContractRecord? existing = FindByIdempotencyKeyNoLock(idempotencyKey);
            if (existing is not null)
            {
                return existing;
            }

            string contractId = string.IsNullOrWhiteSpace(requestedContractId)
                ? "mercenary-" + Guid.NewGuid().ToString("N")
                : requestedContractId!;
            if (contracts.ContainsKey(contractId))
            {
                contractId = "mercenary-" + Guid.NewGuid().ToString("N");
            }
            long expiresAtGameTicks = createdAtGameTicks + durationDays * BankLoanPolicy.GameTicksPerDay;
            var contract = new MercenaryContractRecord(
                contractId,
                idempotencyKey,
                userId,
                colonyId,
                snapshotId,
                skillDefName,
                skillLevel,
                durationDays,
                priceSilver,
                harmfulSurgeryFineSilver,
                deathFineSilver,
                createdAtGameTicks,
                expiresAtGameTicks,
                createdAtUtc,
                StatusPendingActivation,
                ActivatedAtUtc: null);
            contracts[contractId] = contract;
            idByIdempotencyKey[idempotencyKey] = contractId;
            RegisterOpenContract(contract);
            SaveLocked();
            return contract;
        }
    }

    public MercenaryContractRecord? ActivateWithSnapshot(
        string idempotencyKey,
        string userId,
        string colonyId,
        string contractId,
        string snapshotId,
        string skillDefName,
        int skillLevel,
        int durationDays,
        int priceSilver,
        int harmfulSurgeryFineSilver,
        int deathFineSilver,
        long createdAtGameTicks,
        DateTimeOffset createdAtUtc)
    {
        lock (gate)
        {
            MercenaryContractRecord? existing = FindByIdempotencyKeyNoLock(idempotencyKey);
            if (existing is not null)
            {
                return existing;
            }

            string resolvedContractId = string.IsNullOrWhiteSpace(contractId)
                ? "mercenary-" + Guid.NewGuid().ToString("N")
                : contractId;
            if (contracts.ContainsKey(resolvedContractId))
            {
                return null;
            }

            long expiresAtGameTicks = createdAtGameTicks + durationDays * BankLoanPolicy.GameTicksPerDay;
            var contract = new MercenaryContractRecord(
                resolvedContractId,
                idempotencyKey,
                userId,
                colonyId,
                snapshotId,
                skillDefName,
                skillLevel,
                durationDays,
                priceSilver,
                harmfulSurgeryFineSilver,
                deathFineSilver,
                createdAtGameTicks,
                expiresAtGameTicks,
                createdAtUtc,
                StatusActive,
                ActivatedAtUtc: createdAtUtc);
            contracts[resolvedContractId] = contract;
            idByIdempotencyKey[idempotencyKey] = resolvedContractId;
            RegisterOpenContract(contract);
            SaveLocked();
            return contract;
        }
    }

    public MercenaryConfirmationResult ConfirmPendingForSnapshot(
        string userId,
        string colonyId,
        string acceptedSnapshotId,
        DateTimeOffset confirmedAtUtc)
    {
        lock (gate)
        {
            List<MercenaryContractRecord> confirmed = new();
            foreach (MercenaryContractRecord contract in contracts.Values
                         .Where(contract => string.Equals(contract.UserId, userId, StringComparison.Ordinal)
                             && string.Equals(contract.ColonyId, colonyId, StringComparison.Ordinal)
                             && string.Equals(contract.Status, StatusPendingActivation, StringComparison.Ordinal))
                         .ToList())
            {
                MercenaryContractRecord active = contract with
                {
                    Status = StatusActive,
                    SnapshotId = acceptedSnapshotId,
                    ActivatedAtUtc = confirmedAtUtc
                };
                contracts[contract.ContractId] = active;
                confirmed.Add(active);
            }

            if (confirmed.Count > 0)
            {
                SaveLocked();
            }

            return new MercenaryConfirmationResult(confirmed);
        }
    }

    public MercenaryPendingConfirmationReconciliationResult ReconcilePendingConfirmations(
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        TimeSpan timeout,
        bool forceCancel)
    {
        lock (gate)
        {
            List<MercenaryContractRecord> pending = contracts.Values
                .Where(contract => string.Equals(contract.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(contract.ColonyId, colonyId, StringComparison.Ordinal)
                    && string.Equals(contract.Status, StatusPendingActivation, StringComparison.Ordinal)
                    && ShouldCancelPending(contract.CreatedAtUtc, nowUtc, timeout, forceCancel))
                .ToList();
            foreach (MercenaryContractRecord contract in pending)
            {
                contracts.Remove(contract.ContractId);
                idByIdempotencyKey.Remove(contract.IdempotencyKey);
                UnregisterOpenContract(contract);
            }

            if (pending.Count > 0)
            {
                SaveLocked();
            }

            return new MercenaryPendingConfirmationReconciliationResult(pending.Count);
        }
    }

    public MercenaryContractRecord? Complete(
        string contractId,
        string userId,
        string colonyId)
    {
        lock (gate)
        {
            if (!contracts.TryGetValue(contractId, out MercenaryContractRecord? contract)
                || !string.Equals(contract.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(contract.ColonyId, colonyId, StringComparison.Ordinal))
            {
                return null;
            }

            MercenaryContractRecord completed = contract with
            {
                Status = StatusCompleted
            };
            contracts[contract.ContractId] = completed;
            UnregisterOpenContract(contract);
            SaveLocked();
            return completed;
        }
    }

    public int CloseForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            List<MercenaryContractRecord> open = contracts.Values
                .Where(contract => string.Equals(contract.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(contract.ColonyId, colonyId, StringComparison.Ordinal)
                    && !string.Equals(contract.Status, StatusCompleted, StringComparison.Ordinal))
                .ToList();
            foreach (MercenaryContractRecord contract in open)
            {
                contracts[contract.ContractId] = contract with
                {
                    Status = StatusCompleted
                };
                UnregisterOpenContract(contract);
            }

            if (open.Count > 0)
            {
                SaveLocked();
            }

            return open.Count;
        }
    }

    private MercenaryContractRecord? FindByIdempotencyKeyNoLock(string idempotencyKey)
    {
        return idByIdempotencyKey.TryGetValue(idempotencyKey, out string? contractId)
            && contracts.TryGetValue(contractId, out MercenaryContractRecord? contract)
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
                MercenaryContractRecord? contract = JsonSerializer.Deserialize<MercenaryContractRecord>(pair.Value, JsonOptions);
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
            MercenaryContractRegistryPersistence? persisted =
                JsonSerializer.Deserialize<MercenaryContractRegistryPersistence>(json, JsonOptions);
            if (persisted?.Contracts is null)
            {
                return false;
            }

            bool imported = false;
            foreach (MercenaryContractRecord contract in persisted.Contracts)
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
            new MercenaryContractRegistryPersistence(contracts.Values
                .OrderBy(contract => contract.CreatedAtUtc)
                .ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private sealed record MercenaryContractRegistryPersistence(IReadOnlyList<MercenaryContractRecord> Contracts);

    private bool RegisterLoadedContract(MercenaryContractRecord contract, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(contract.ContractId)
            || string.IsNullOrWhiteSpace(contract.IdempotencyKey)
            || string.IsNullOrWhiteSpace(contract.UserId)
            || string.IsNullOrWhiteSpace(contract.ColonyId))
        {
            return false;
        }

        if (contracts.TryGetValue(contract.ContractId, out MercenaryContractRecord? existing))
        {
            if (!overwrite)
            {
                return false;
            }

            UnregisterOpenContract(existing);
        }

        contracts[contract.ContractId] = contract;
        idByIdempotencyKey[contract.IdempotencyKey] = contract.ContractId;
        RegisterOpenContract(contract);
        return true;
    }

    private void RegisterOpenContract(MercenaryContractRecord contract)
    {
        if (!IsOpenStatus(contract.Status))
        {
            return;
        }

        string key = ColonyKey(contract.UserId, contract.ColonyId);
        openContractCountByColony[key] = openContractCountByColony.TryGetValue(key, out int count)
            ? count + 1
            : 1;
    }

    private void UnregisterOpenContract(MercenaryContractRecord contract)
    {
        if (!IsOpenStatus(contract.Status))
        {
            return;
        }

        string key = ColonyKey(contract.UserId, contract.ColonyId);
        if (!openContractCountByColony.TryGetValue(key, out int count))
        {
            return;
        }

        if (count <= 1)
        {
            openContractCountByColony.Remove(key);
            return;
        }

        openContractCountByColony[key] = count - 1;
    }

    private static bool IsOpenStatus(string? status)
    {
        return string.Equals(status, StatusActive, StringComparison.Ordinal)
            || string.Equals(status, StatusPendingActivation, StringComparison.Ordinal);
    }

    private static string ColonyKey(string userId, string colonyId)
    {
        return userId + "\u001f" + colonyId;
    }

    private static bool ShouldCancelPending(
        DateTimeOffset requestedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan timeout,
        bool forceCancel)
    {
        return forceCancel || timeout <= TimeSpan.Zero || requestedAtUtc <= nowUtc - timeout;
    }

    private static string ContractRowKey(string contractId)
    {
        return "contract:" + contractId;
    }
}

public sealed record MercenaryContractRecord(
    string ContractId,
    string IdempotencyKey,
    string UserId,
    string ColonyId,
    string SnapshotId,
    string SkillDefName,
    int SkillLevel,
    int DurationDays,
    int PriceSilver,
    int HarmfulSurgeryFineSilver,
    int DeathFineSilver,
    long CreatedAtGameTicks,
    long ExpiresAtGameTicks,
    DateTimeOffset CreatedAtUtc,
    string Status,
    DateTimeOffset? ActivatedAtUtc);

public sealed record MercenaryConfirmationResult(IReadOnlyList<MercenaryContractRecord> Contracts);

public sealed record MercenaryPendingConfirmationReconciliationResult(int CancelledActivations)
{
    public bool Changed => CancelledActivations > 0;
}
