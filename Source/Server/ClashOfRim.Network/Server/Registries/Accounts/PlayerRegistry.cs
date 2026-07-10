namespace AIRsLight.ClashOfRim.Network;

public sealed class PlayerRegistry
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly IPlayerRegistryPersistenceStore? structuredPersistence;
    private readonly Dictionary<string, PlayerSessionRecord> recordsByUserId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerColonyTombstoneRecord> tombstonesByInstance = new(StringComparer.Ordinal);
    private IReadOnlyList<PlayerSessionRecord>? sortedRecordsCache;
    private IReadOnlyList<PlayerColonyTombstoneRecord>? sortedTombstonesCache;

    public PlayerRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    public PlayerRegistry(string? persistencePath)
        : this(string.IsNullOrWhiteSpace(persistencePath) ? null : new FileJsonPersistenceSlot(persistencePath))
    {
    }

    internal PlayerRegistry(IJsonPersistenceSlot? persistence)
    {
        legacyPersistence = persistence;
        Load();
    }

    internal PlayerRegistry(
        IPlayerRegistryPersistenceStore structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public string ResolveActiveColonyId(string userId, string requestedColonyId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedColonyId);

        lock (gate)
        {
            if (recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? active))
            {
                return active.ColonyId;
            }

            if (!tombstonesByInstance.ContainsKey(InstanceKey(userId, requestedColonyId)))
            {
                return requestedColonyId;
            }

            return CreateReplacementColonyId(requestedColonyId, nowUtc);
        }
    }

    public void Record(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset seenAtUtc,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            if (tombstonesByInstance.ContainsKey(InstanceKey(userId, colonyId)))
            {
                return;
            }

            string? resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? current)
                    ? current.DisplayName
                    : null
                : displayName.Trim();
            recordsByUserId[userId] = new PlayerSessionRecord(
                userId,
                colonyId,
                currentSnapshotId,
                seenAtUtc,
                resolvedDisplayName,
                recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? currentForWealth)
                    && string.Equals(currentForWealth.ColonyId, colonyId, StringComparison.Ordinal)
                        ? currentForWealth.LatestSnapshotWealth
                        : null,
                currentForWealth is not null && string.Equals(currentForWealth.ColonyId, colonyId, StringComparison.Ordinal)
                    ? currentForWealth.LatestSnapshotWealthSnapshotId
                    : null);
            InvalidateSortedCachesLocked();
            SaveLocked();
        }
    }

    public void RecordLatestSnapshotReference(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            if (tombstonesByInstance.ContainsKey(InstanceKey(userId, colonyId)))
            {
                return;
            }

            recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? current);
            bool sameColony = current is not null && string.Equals(current.ColonyId, colonyId, StringComparison.Ordinal);
            bool sameWealthSnapshot = sameColony
                && !string.IsNullOrWhiteSpace(currentSnapshotId)
                && string.Equals(current!.LatestSnapshotWealthSnapshotId, currentSnapshotId, StringComparison.Ordinal);
            recordsByUserId[userId] = new PlayerSessionRecord(
                userId,
                colonyId,
                currentSnapshotId,
                seenAtUtc,
                sameColony ? current!.DisplayName : null,
                sameWealthSnapshot ? current!.LatestSnapshotWealth : null,
                sameWealthSnapshot ? current!.LatestSnapshotWealthSnapshotId : null);
            InvalidateSortedCachesLocked();
            SaveLocked();
        }
    }

    public void RecordLatestSnapshotWealth(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        int? latestSnapshotWealth,
        DateTimeOffset seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            if (tombstonesByInstance.ContainsKey(InstanceKey(userId, colonyId)))
            {
                return;
            }

            recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? current);
            bool sameColony = current is not null && string.Equals(current.ColonyId, colonyId, StringComparison.Ordinal);
            recordsByUserId[userId] = new PlayerSessionRecord(
                userId,
                colonyId,
                currentSnapshotId,
                seenAtUtc,
                sameColony ? current!.DisplayName : null,
                latestSnapshotWealth,
                currentSnapshotId);
            InvalidateSortedCachesLocked();
            SaveLocked();
        }
    }

    public IReadOnlyList<PlayerSessionRecord> List()
    {
        lock (gate)
        {
            return SortedRecordsLocked();
        }
    }

    public PlayerSessionRecord? FindByUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        lock (gate)
        {
            return recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? record)
                ? record
                : null;
        }
    }

    public bool ContainsUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return recordsByUserId.ContainsKey(userId);
        }
    }

    public bool Remove(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (gate)
        {
            bool removed = recordsByUserId.Remove(userId);
            if (removed)
            {
                InvalidateSortedCachesLocked();
                SaveLocked();
            }

            return removed;
        }
    }

    public void MarkDeleted(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        DateTimeOffset deletedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            if (recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? active)
                && string.Equals(active.ColonyId, colonyId, StringComparison.Ordinal))
            {
                recordsByUserId.Remove(userId);
            }

            tombstonesByInstance[InstanceKey(userId, colonyId)] = new PlayerColonyTombstoneRecord(
                userId,
                colonyId,
                currentSnapshotId,
                deletedAtUtc);
            InvalidateSortedCachesLocked();
            SaveLocked();
        }
    }

    public bool IsDeleted(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            return tombstonesByInstance.ContainsKey(InstanceKey(userId, colonyId));
        }
    }

    public IReadOnlyList<PlayerColonyTombstoneRecord> ListDeleted()
    {
        lock (gate)
        {
            return SortedTombstonesLocked();
        }
    }

    private static string InstanceKey(string userId, string colonyId)
    {
        return userId + "\n" + colonyId;
    }

    private static string CreateReplacementColonyId(string requestedColonyId, DateTimeOffset nowUtc)
    {
        string prefix = string.IsNullOrWhiteSpace(requestedColonyId)
            ? "colony"
            : requestedColonyId.Trim();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        return prefix
            + "-rebuild-"
            + nowUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + suffix;
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool changed = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (changed && structuredPersistence is not null)
        {
            SaveLocked();
        }
    }

    private bool LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return false;
        }

        bool changed = false;
        foreach (PlayerSessionRecord record in structuredPersistence.ReadPlayers())
        {
            if (string.IsNullOrWhiteSpace(record.UserId)
                || string.IsNullOrWhiteSpace(record.ColonyId))
            {
                continue;
            }

            recordsByUserId[record.UserId] = record;
            changed = true;
        }

        foreach (PlayerColonyTombstoneRecord tombstone in structuredPersistence.ReadTombstones())
        {
            if (string.IsNullOrWhiteSpace(tombstone.UserId)
                || string.IsNullOrWhiteSpace(tombstone.ColonyId))
            {
                continue;
            }

            tombstonesByInstance[InstanceKey(tombstone.UserId, tombstone.ColonyId)] = tombstone;
            if (recordsByUserId.TryGetValue(tombstone.UserId, out PlayerSessionRecord? active)
                && string.Equals(active.ColonyId, tombstone.ColonyId, StringComparison.Ordinal))
            {
                recordsByUserId.Remove(tombstone.UserId);
            }

            changed = true;
        }

        if (changed)
        {
            InvalidateSortedCachesLocked();
        }

        return false;
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
            bool changed = false;
            PlayerRegistryPersistence? persisted =
                System.Text.Json.JsonSerializer.Deserialize<PlayerRegistryPersistence>(json, JsonOptions);
            if (persisted?.Players is not null)
            {
                foreach (PlayerSessionRecord record in persisted.Players)
                {
                    if (string.IsNullOrWhiteSpace(record.UserId)
                        || string.IsNullOrWhiteSpace(record.ColonyId))
                    {
                        continue;
                    }

                    if (!recordsByUserId.ContainsKey(record.UserId))
                    {
                        recordsByUserId[record.UserId] = record;
                        changed = true;
                    }
                }
            }

            if (persisted?.Tombstones is not null)
            {
                foreach (PlayerColonyTombstoneRecord tombstone in persisted.Tombstones)
                {
                    if (string.IsNullOrWhiteSpace(tombstone.UserId)
                        || string.IsNullOrWhiteSpace(tombstone.ColonyId))
                    {
                        continue;
                    }

                    string key = InstanceKey(tombstone.UserId, tombstone.ColonyId);
                    if (!tombstonesByInstance.ContainsKey(key))
                    {
                        tombstonesByInstance[key] = tombstone;
                        changed = true;
                    }

                    if (recordsByUserId.TryGetValue(tombstone.UserId, out PlayerSessionRecord? active)
                        && string.Equals(active.ColonyId, tombstone.ColonyId, StringComparison.Ordinal))
                    {
                        recordsByUserId.Remove(tombstone.UserId);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                InvalidateSortedCachesLocked();
            }

            return changed;
        }
        catch (System.Text.Json.JsonException)
        {
            recordsByUserId.Clear();
            tombstonesByInstance.Clear();
            InvalidateSortedCachesLocked();
            return false;
        }
    }

    private void SaveLocked()
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAll(
                SortedRecordsLocked(),
                SortedTombstonesLocked());
            return;
        }

        if (legacyPersistence is null)
        {
            return;
        }

        string json = System.Text.Json.JsonSerializer.Serialize(
            new PlayerRegistryPersistence(
                SortedRecordsLocked(),
                SortedTombstonesLocked()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private IReadOnlyList<PlayerSessionRecord> SortedRecordsLocked()
    {
        sortedRecordsCache ??= recordsByUserId.Values
            .OrderBy(record => record.UserId, StringComparer.Ordinal)
            .ToArray();
        return sortedRecordsCache;
    }

    private IReadOnlyList<PlayerColonyTombstoneRecord> SortedTombstonesLocked()
    {
        sortedTombstonesCache ??= tombstonesByInstance.Values
            .OrderBy(record => record.UserId, StringComparer.Ordinal)
            .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
            .ToArray();
        return sortedTombstonesCache;
    }

    private void InvalidateSortedCachesLocked()
    {
        sortedRecordsCache = null;
        sortedTombstonesCache = null;
    }

    private sealed record PlayerRegistryPersistence(
        IReadOnlyList<PlayerSessionRecord> Players,
        IReadOnlyList<PlayerColonyTombstoneRecord> Tombstones);
}

public sealed record PlayerSessionRecord(
    string UserId,
    string ColonyId,
    string? CurrentSnapshotId,
    DateTimeOffset LastSeenAtUtc,
    string? DisplayName = null,
    int? LatestSnapshotWealth = null,
    string? LatestSnapshotWealthSnapshotId = null);

public sealed record PlayerColonyTombstoneRecord(
    string UserId,
    string ColonyId,
    string? LastSnapshotId,
    DateTimeOffset DeletedAtUtc);
