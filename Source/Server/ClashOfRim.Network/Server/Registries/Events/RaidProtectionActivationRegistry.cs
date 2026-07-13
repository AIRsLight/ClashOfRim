using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RaidProtectionActivationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, RaidProtectionActivationRecord> activations = new(StringComparer.Ordinal);

    public RaidProtectionActivationRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal RaidProtectionActivationRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal RaidProtectionActivationRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public DateTimeOffset? FindActivatedAt(
        string raidEventId,
        string defenderUserId,
        string? defenderColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raidEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);

        lock (gate)
        {
            if (!activations.TryGetValue(raidEventId, out RaidProtectionActivationRecord? record))
            {
                return null;
            }

            if (!string.Equals(record.DefenderUserId, defenderUserId, StringComparison.Ordinal)
                || !string.Equals(record.DefenderColonyId ?? string.Empty, defenderColonyId ?? string.Empty, StringComparison.Ordinal))
            {
                return null;
            }

            return record.ActivatedAtUtc;
        }
    }

    public bool ActivateIfMissing(
        string raidEventId,
        string defenderUserId,
        string? defenderColonyId,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raidEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);

        lock (gate)
        {
            if (activations.ContainsKey(raidEventId))
            {
                return false;
            }

            var record = new RaidProtectionActivationRecord(
                raidEventId,
                defenderUserId,
                string.IsNullOrWhiteSpace(defenderColonyId) ? null : defenderColonyId,
                activatedAtUtc);
            PersistChanges(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [raidEventId] = JsonSerializer.Serialize(record, JsonOptions)
                },
                Array.Empty<string>());
            activations[raidEventId] = record;
            return true;
        }
    }

    public int RemoveForColony(string defenderUserId, string? defenderColonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);

        lock (gate)
        {
            List<string>? removed = null;
            foreach (KeyValuePair<string, RaidProtectionActivationRecord> pair in activations)
            {
                RaidProtectionActivationRecord record = pair.Value;
                if (string.Equals(record.DefenderUserId, defenderUserId, StringComparison.Ordinal)
                    && string.Equals(record.DefenderColonyId ?? string.Empty, defenderColonyId ?? string.Empty, StringComparison.Ordinal))
                {
                    removed ??= new List<string>();
                    removed.Add(pair.Key);
                }
            }

            if (removed is null)
            {
                return 0;
            }

            PersistChanges(new Dictionary<string, string>(StringComparer.Ordinal), removed);
            foreach (string key in removed)
            {
                activations.Remove(key);
            }
            return removed.Count;
        }
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAllForImport(activations.ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.Serialize(pair.Value, JsonOptions),
                StringComparer.Ordinal));
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
                RaidProtectionActivationRecord? record =
                    JsonSerializer.Deserialize<RaidProtectionActivationRecord>(pair.Value, JsonOptions);
                if (record is null
                    || string.IsNullOrWhiteSpace(record.RaidEventId)
                    || string.IsNullOrWhiteSpace(record.DefenderUserId))
                {
                    continue;
                }

                activations[record.RaidEventId] = record;
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

        RaidProtectionActivationRegistryPersistence? persisted =
            JsonSerializer.Deserialize<RaidProtectionActivationRegistryPersistence>(json, JsonOptions);
        if (persisted?.Activations is null)
        {
            return false;
        }

        bool imported = false;
        foreach (RaidProtectionActivationRecord record in persisted.Activations)
        {
            if (string.IsNullOrWhiteSpace(record.RaidEventId)
                || string.IsNullOrWhiteSpace(record.DefenderUserId))
            {
                continue;
            }

            if (activations.ContainsKey(record.RaidEventId))
            {
                continue;
            }

            activations[record.RaidEventId] = record;
            imported = true;
        }

        return imported;
    }

    private void PersistChanges(
        IReadOnlyDictionary<string, string> upserts,
        IReadOnlyCollection<string> deletes)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ApplyBatch(upserts, deletes);
            return;
        }

        if (legacyPersistence is null)
        {
            return;
        }

        Dictionary<string, RaidProtectionActivationRecord> next = new(activations, StringComparer.Ordinal);
        foreach (string key in deletes)
        {
            next.Remove(key);
        }

        foreach (KeyValuePair<string, string> pair in upserts)
        {
            RaidProtectionActivationRecord? record = JsonSerializer.Deserialize<RaidProtectionActivationRecord>(pair.Value, JsonOptions);
            if (record is not null)
            {
                next[pair.Key] = record;
            }
        }

        string json = JsonSerializer.Serialize(
            new RaidProtectionActivationRegistryPersistence(next.Values.ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private sealed record RaidProtectionActivationRegistryPersistence(
        IReadOnlyList<RaidProtectionActivationRecord> Activations);
}

public sealed record RaidProtectionActivationRecord(
    string RaidEventId,
    string DefenderUserId,
    string? DefenderColonyId,
    DateTimeOffset ActivatedAtUtc);
