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
            var record = new PlayerSessionRecord(
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
            PublishPlayers([record], Array.Empty<string>());
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
            var record = new PlayerSessionRecord(
                userId,
                colonyId,
                currentSnapshotId,
                seenAtUtc,
                sameColony ? current!.DisplayName : null,
                sameWealthSnapshot ? current!.LatestSnapshotWealth : null,
                sameWealthSnapshot ? current!.LatestSnapshotWealthSnapshotId : null);
            PublishPlayers([record], Array.Empty<string>());
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
            var record = new PlayerSessionRecord(
                userId,
                colonyId,
                currentSnapshotId,
                seenAtUtc,
                sameColony ? current!.DisplayName : null,
                latestSnapshotWealth,
                currentSnapshotId);
            PublishPlayers([record], Array.Empty<string>());
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
            if (recordsByUserId.ContainsKey(userId))
            {
                PublishPlayers(Array.Empty<PlayerSessionRecord>(), [userId]);
                return true;
            }

            return false;
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
            bool removeActive = recordsByUserId.TryGetValue(userId, out PlayerSessionRecord? active)
                && string.Equals(active.ColonyId, colonyId, StringComparison.Ordinal);
            var tombstone = new PlayerColonyTombstoneRecord(
                userId,
                colonyId,
                currentSnapshotId,
                deletedAtUtc);
            PublishPlayerChanges(
                Array.Empty<PlayerSessionRecord>(),
                removeActive ? [userId] : Array.Empty<string>(),
                [tombstone],
                Array.Empty<(string UserId, string ColonyId)>());
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
            structuredPersistence.ReplaceAllForImport(
                SortedRecordsLocked(),
                SortedTombstonesLocked());
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

    private void PublishPlayers(
        IReadOnlyCollection<PlayerSessionRecord> upserts,
        IReadOnlyCollection<string> deletes)
        => PublishPlayerChanges(upserts, deletes, [], []);

    private void PublishPlayerChanges(
        IReadOnlyCollection<PlayerSessionRecord> playerUpserts,
        IReadOnlyCollection<string> playerDeletes,
        IReadOnlyCollection<PlayerColonyTombstoneRecord> tombstoneUpserts,
        IReadOnlyCollection<(string UserId, string ColonyId)> tombstoneDeletes)
    {
        void Apply(Dictionary<string, PlayerSessionRecord> players, Dictionary<string, PlayerColonyTombstoneRecord> tombstones)
        {
            foreach (var user in playerDeletes) players.Remove(user);
            foreach (var player in playerUpserts) players[player.UserId] = player;
            foreach (var key in tombstoneDeletes) tombstones.Remove(InstanceKey(key.UserId, key.ColonyId));
            foreach (var tombstone in tombstoneUpserts) tombstones[InstanceKey(tombstone.UserId, tombstone.ColonyId)] = tombstone;
        }

        if (structuredPersistence is not null)
            structuredPersistence.ApplyBatch(playerUpserts, playerDeletes, tombstoneUpserts, tombstoneDeletes);
        else if (legacyPersistence is not null)
        {
            var nextPlayers = new Dictionary<string, PlayerSessionRecord>(recordsByUserId, StringComparer.Ordinal);
            var nextTombstones = new Dictionary<string, PlayerColonyTombstoneRecord>(tombstonesByInstance, StringComparer.Ordinal);
            Apply(nextPlayers, nextTombstones);
            legacyPersistence.Write(System.Text.Json.JsonSerializer.Serialize(new PlayerRegistryPersistence(
                nextPlayers.Values.OrderBy(record => record.UserId, StringComparer.Ordinal).ToArray(),
                nextTombstones.Values.OrderBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.ColonyId, StringComparer.Ordinal).ToArray()), JsonOptions));
        }
        Apply(recordsByUserId, tombstonesByInstance);
        InvalidateSortedCachesLocked();
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
