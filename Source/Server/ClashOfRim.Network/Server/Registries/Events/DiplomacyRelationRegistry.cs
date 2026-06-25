using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class DiplomacyRelationRegistry
{
    public const string RelationAlly = "Ally";
    public const string RelationHostile = "Hostile";
    public const string RelationNeutral = "Neutral";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, DiplomacyRelationRecord> records = new(StringComparer.Ordinal);

    public DiplomacyRelationRegistry(string? persistencePath = null)
        : this(string.IsNullOrWhiteSpace(persistencePath) ? null : new FileJsonPersistenceSlot(persistencePath))
    {
    }

    internal DiplomacyRelationRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public string GetRelationKind(
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        if (string.IsNullOrWhiteSpace(userA) || string.IsNullOrWhiteSpace(userB))
        {
            return RelationNeutral;
        }

        lock (gate)
        {
            return records.TryGetValue(RelationKey(userA, colonyA, userB, colonyB), out DiplomacyRelationRecord? record)
                ? record.RelationKind
                : RelationNeutral;
        }
    }

    public DiplomacyRelationRecord? GetRelation(
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        if (string.IsNullOrWhiteSpace(userA) || string.IsNullOrWhiteSpace(userB))
        {
            return null;
        }

        lock (gate)
        {
            return records.TryGetValue(RelationKey(userA, colonyA, userB, colonyB), out DiplomacyRelationRecord? record)
                ? record
                : null;
        }
    }

    public DiplomacyRelationRecord SetRelationKind(
        string userA,
        string? colonyA,
        string userB,
        string? colonyB,
        string relationKind,
        string? sourceEventId,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userA);
        ArgumentException.ThrowIfNullOrWhiteSpace(userB);

        string normalized = NormalizeRelationKind(relationKind);
        RelationEndpoint first = new(userA, NormalizeColonyId(colonyA));
        RelationEndpoint second = new(userB, NormalizeColonyId(colonyB));
        if (string.CompareOrdinal(EndpointKey(second), EndpointKey(first)) < 0)
        {
            (first, second) = (second, first);
        }

        var record = new DiplomacyRelationRecord(
            first.UserId,
            first.ColonyId,
            second.UserId,
            second.ColonyId,
            normalized,
            sourceEventId,
            updatedAtUtc);

        lock (gate)
        {
            records[RelationKey(first, second)] = record;
            SaveLocked();
            return record;
        }
    }

    public IReadOnlyList<DiplomacyRelationRecord> List()
    {
        lock (gate)
        {
            return records.Values
                .OrderBy(record => record.UserA, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyA, StringComparer.Ordinal)
                .ThenBy(record => record.UserB, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyB, StringComparer.Ordinal)
                .ToList();
        }
    }

    public int RemoveForColony(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            List<string> removedKeys = records
                .Where(pair => IsEndpoint(pair.Value.UserA, pair.Value.ColonyA, userId, colonyId)
                    || IsEndpoint(pair.Value.UserB, pair.Value.ColonyB, userId, colonyId))
                .Select(pair => pair.Key)
                .ToList();
            foreach (string key in removedKeys)
            {
                records.Remove(key);
            }

            if (removedKeys.Count > 0)
            {
                SaveLocked();
            }

            return removedKeys.Count;
        }
    }

    private static string NormalizeRelationKind(string relationKind)
    {
        if (string.Equals(relationKind, RelationAlly, StringComparison.OrdinalIgnoreCase))
        {
            return RelationAlly;
        }

        if (string.Equals(relationKind, RelationHostile, StringComparison.OrdinalIgnoreCase))
        {
            return RelationHostile;
        }

        return RelationNeutral;
    }

    private void Load()
    {
        if (persistence is null)
        {
            return;
        }

        try
        {
            string? json = persistence.Read();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            DiplomacyRelationRegistryPersistence? persisted =
                JsonSerializer.Deserialize<DiplomacyRelationRegistryPersistence>(json, JsonOptions);
            if (persisted?.Relations is null)
            {
                return;
            }

            foreach (DiplomacyRelationRecord record in persisted.Relations)
            {
                if (string.IsNullOrWhiteSpace(record.UserA) || string.IsNullOrWhiteSpace(record.UserB))
                {
                    continue;
                }

                records[RelationKey(record.UserA, record.ColonyA, record.UserB, record.ColonyB)] =
                    record with { RelationKind = NormalizeRelationKind(record.RelationKind) };
            }
        }
        catch (JsonException)
        {
            records.Clear();
        }
        catch (IOException)
        {
            records.Clear();
        }
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new DiplomacyRelationRegistryPersistence(records.Values
                .OrderBy(record => record.UserA, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyA, StringComparer.Ordinal)
                .ThenBy(record => record.UserB, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyB, StringComparer.Ordinal)
                .ToList()),
            JsonOptions);
        persistence.Write(json);
    }

    private static string RelationKey(string userA, string? colonyA, string userB, string? colonyB)
    {
        RelationEndpoint first = new(userA, NormalizeColonyId(colonyA));
        RelationEndpoint second = new(userB, NormalizeColonyId(colonyB));
        return string.CompareOrdinal(EndpointKey(first), EndpointKey(second)) <= 0
            ? RelationKey(first, second)
            : RelationKey(second, first);
    }

    private static string RelationKey(RelationEndpoint first, RelationEndpoint second)
    {
        return EndpointKey(first) + "\n" + EndpointKey(second);
    }

    private static string EndpointKey(RelationEndpoint endpoint)
    {
        return endpoint.UserId + "\n" + endpoint.ColonyId;
    }

    private static string NormalizeColonyId(string? colonyId)
    {
        return string.IsNullOrWhiteSpace(colonyId) ? string.Empty : colonyId!;
    }

    private static bool IsEndpoint(string actualUserId, string actualColonyId, string expectedUserId, string expectedColonyId)
    {
        return string.Equals(actualUserId, expectedUserId, StringComparison.Ordinal)
            && string.Equals(actualColonyId, expectedColonyId, StringComparison.Ordinal);
    }

    private sealed record RelationEndpoint(string UserId, string ColonyId);

    private sealed record DiplomacyRelationRegistryPersistence(IReadOnlyList<DiplomacyRelationRecord> Relations);
}

public sealed record DiplomacyRelationRecord(
    string UserA,
    string ColonyA,
    string UserB,
    string ColonyB,
    string RelationKind,
    string? SourceEventId,
    DateTimeOffset UpdatedAtUtc);
