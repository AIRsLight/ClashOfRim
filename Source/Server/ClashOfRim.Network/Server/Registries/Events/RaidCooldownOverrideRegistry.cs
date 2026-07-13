using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RaidCooldownOverrideRegistry
{
    private readonly object gate = new();
    private readonly IRaidCooldownOverridePersistenceStore? persistence;
    private readonly Dictionary<string, RaidCooldownOverrideRecord> overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> suppressedRaidEventIds = new(StringComparer.Ordinal);

    public RaidCooldownOverrideRegistry()
    {
    }

    internal RaidCooldownOverrideRegistry(IRaidCooldownOverridePersistenceStore persistence)
    {
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        foreach (RaidCooldownOverrideRecord record in persistence.ReadOverrides())
        {
            overrides[ColonyKey(record.DefenderUserId, record.DefenderColonyId)] = record;
        }

        foreach (RaidCooldownSuppressionRecord record in persistence.ReadSuppressions())
        {
            string key = ColonyKey(record.DefenderUserId, record.DefenderColonyId);
            if (!suppressedRaidEventIds.TryGetValue(key, out HashSet<string>? eventIds))
            {
                eventIds = new HashSet<string>(StringComparer.Ordinal);
                suppressedRaidEventIds[key] = eventIds;
            }

            eventIds.Add(record.RaidEventId);
        }
    }

    public RaidCooldownOverrideRecord SetCurrent(
        string defenderUserId,
        string defenderColonyId,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset cooldownUntilUtc,
        IReadOnlyCollection<string> replacedRaidEventIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderColonyId);
        ArgumentNullException.ThrowIfNull(replacedRaidEventIds);

        var record = new RaidCooldownOverrideRecord(
            defenderUserId,
            defenderColonyId,
            updatedAtUtc,
            cooldownUntilUtc);
        List<RaidCooldownSuppressionRecord> suppressions = replacedRaidEventIds
            .Where(eventId => !string.IsNullOrWhiteSpace(eventId))
            .Distinct(StringComparer.Ordinal)
            .Select(eventId => new RaidCooldownSuppressionRecord(
                defenderUserId,
                defenderColonyId,
                eventId,
                updatedAtUtc))
            .ToList();

        lock (gate)
        {
            persistence?.SetCurrent(record, suppressions);
            string key = ColonyKey(defenderUserId, defenderColonyId);
            overrides[key] = record;
            if (!suppressedRaidEventIds.TryGetValue(key, out HashSet<string>? eventIds))
            {
                eventIds = new HashSet<string>(StringComparer.Ordinal);
                suppressedRaidEventIds[key] = eventIds;
            }

            eventIds.UnionWith(suppressions.Select(suppression => suppression.RaidEventId));
            return record;
        }
    }

    public DateTimeOffset? ResolveCurrentUntil(
        string defenderUserId,
        string defenderColonyId,
        DateTimeOffset nowUtc,
        IEnumerable<RaidCooldownRecord> projectedRecords)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderColonyId);
        ArgumentNullException.ThrowIfNull(projectedRecords);

        lock (gate)
        {
            string key = ColonyKey(defenderUserId, defenderColonyId);
            suppressedRaidEventIds.TryGetValue(key, out HashSet<string>? suppressed);
            DateTimeOffset? effectiveUntilUtc = projectedRecords
                .Where(record => record.CooldownUntilUtc > nowUtc)
                .Where(record => suppressed is null || !suppressed.Contains(record.RaidEventId))
                .Select(record => (DateTimeOffset?)record.CooldownUntilUtc)
                .Max();

            if (overrides.TryGetValue(key, out RaidCooldownOverrideRecord? current)
                && current.CooldownUntilUtc > nowUtc
                && (!effectiveUntilUtc.HasValue || current.CooldownUntilUtc > effectiveUntilUtc.Value))
            {
                effectiveUntilUtc = current.CooldownUntilUtc;
            }

            return effectiveUntilUtc;
        }
    }

    private static string ColonyKey(string userId, string colonyId)
    {
        return userId + "\n" + colonyId;
    }
}

public sealed record RaidCooldownOverrideRecord(
    string DefenderUserId,
    string DefenderColonyId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset CooldownUntilUtc);

internal sealed record RaidCooldownSuppressionRecord(
    string DefenderUserId,
    string DefenderColonyId,
    string RaidEventId,
    DateTimeOffset SuppressedAtUtc);
