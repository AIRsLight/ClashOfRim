using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public sealed class AchievementRegistry
{
    public const string AggregationMax = "Max";
    public const string AggregationSum = "Sum";
    public const string CategorySnapshot = "Snapshot";
    public const string CategoryEndgame = "Endgame";
    public const string CategoryEvent = "Event";
    public const string MetricColonyWealth = "colony_wealth";
    public const string MetricPlayerColonists = "player_colonists";
    public const string MetricWealthItems = "wealth_items";
    public const string MetricWealthBuildings = "wealth_buildings";
    public const string MetricWealthPawns = "wealth_pawns";
    public const string MetricPrisoners = "prisoners";
    public const string MetricColonistMood = "colonist_mood";
    public const string MetricColonyRelocations = "colony_relocations";
    public const string MetricCompletedTradeCount = "completed_trade_count";
    public const string MetricCompletedTradeValue = "completed_trade_value";
    public const string MetricColonistsLaunched = "colonists_launched";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly List<AchievementDefinition> definitions = new();
    private readonly object definitionsGate = new();

    private static IEnumerable<AchievementDefinition> BuiltInDefinitions()
    {
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "wealth_500000",
            metricId: MetricColonyWealth,
            threshold: 500_000,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.Wealth500k",
            iconId: null,
            color: AchievementColors.Blue);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "colonists_20",
            metricId: MetricPlayerColonists,
            threshold: 20,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.Colonists20",
            iconId: null,
            color: AchievementColors.Green);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "wealth_items_250000",
            metricId: MetricWealthItems,
            threshold: 250_000,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.WealthItems250k",
            iconId: null,
            color: AchievementColors.Blue);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "wealth_buildings_250000",
            metricId: MetricWealthBuildings,
            threshold: 250_000,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.WealthBuildings250k",
            iconId: null,
            color: AchievementColors.Purple);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "wealth_pawns_200000",
            metricId: MetricWealthPawns,
            threshold: 200_000,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.WealthPawns200k",
            iconId: null,
            color: AchievementColors.Purple);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "prisoners_10",
            metricId: MetricPrisoners,
            threshold: 10,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.Prisoners10",
            iconId: null,
            color: AchievementColors.Red);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "colonist_mood_90",
            metricId: MetricColonistMood,
            threshold: 90,
            category: CategorySnapshot,
            labelKey: "ClashOfRim.Achievement.ColonistMood90",
            iconId: null,
            color: AchievementColors.Green);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "relocations_3",
            metricId: MetricColonyRelocations,
            threshold: 3,
            category: CategoryEvent,
            labelKey: "ClashOfRim.Achievement.Relocations3",
            iconId: null,
            color: AchievementColors.Blue);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "trades_10",
            metricId: MetricCompletedTradeCount,
            threshold: 10,
            category: CategoryEvent,
            labelKey: "ClashOfRim.Achievement.Trades10",
            iconId: null,
            color: AchievementColors.Green);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "trade_value_100000",
            metricId: MetricCompletedTradeValue,
            threshold: 100_000,
            category: CategoryEvent,
            labelKey: "ClashOfRim.Achievement.TradeValue100k",
            iconId: null,
            color: AchievementColors.Purple);
        yield return AchievementDefinition.NumericThreshold(
            achievementId: "waiting_for_the_sun",
            metricId: MetricColonistsLaunched,
            threshold: 1,
            category: CategoryEndgame,
            labelKey: "ClashOfRim.Achievement.WaitingForTheSun",
            iconId: null,
            color: AchievementColors.Purple);
    }

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, AchievementEventRecord> eventsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AchievementAggregateRecord> aggregatesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AchievementMetricEventRecord> metricEventsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AchievementMetricAggregateRecord> metricAggregatesByKey = new(StringComparer.Ordinal);
    private IReadOnlyList<AchievementAggregateRecord>? sortedAggregatesCache;

    public AchievementRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal AchievementRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        RegisterDefinitions(BuiltInDefinitions());
        Load();
    }

    public void RegisterDefinition(AchievementDefinition definition)
    {
        RegisterDefinitions(new[] { definition });
    }

    public void RegisterDefinitions(IEnumerable<AchievementDefinition> definitionsToRegister)
    {
        lock (definitionsGate)
        {
            foreach (AchievementDefinition definition in definitionsToRegister)
            {
                if (!definition.IsValid)
                {
                    continue;
                }

                int existingIndex = definitions.FindIndex(item =>
                    string.Equals(item.AchievementId, definition.AchievementId, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    definitions[existingIndex] = definition;
                }
                else
                {
                    definitions.Add(definition);
                }
            }
        }
    }

    public IReadOnlyList<AchievementEventRecord> RecordSnapshotMetricAchievements(
        string userId,
        string colonyId,
        string snapshotId,
        IReadOnlyDictionary<string, long> metrics,
        IEnumerable<AchievementDefinition>? additionalDefinitions,
        DateTimeOffset recordedAtUtc)
    {
        if (metrics.Count == 0)
        {
            return Array.Empty<AchievementEventRecord>();
        }

        var recorded = new List<AchievementEventRecord>();
        foreach (AchievementDefinition definition in ListDefinitions(additionalDefinitions))
        {
            if (definition.TryBuildCandidate(metrics, out SnapshotAchievementCandidateDto? candidate))
            {
                if (TryRecord(userId, colonyId, snapshotId, candidate!, recordedAtUtc, out AchievementEventRecord? record))
                {
                    recorded.Add(record!);
                }
            }
        }

        return recorded;
    }

    public IReadOnlyList<AchievementEventRecord> RecordCumulativeMetricAchievements(
        string userId,
        string colonyId,
        string sourceEventKey,
        string? sourceSnapshotId,
        IReadOnlyDictionary<string, long> metricDeltas,
        IEnumerable<AchievementDefinition>? additionalDefinitions,
        DateTimeOffset recordedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(sourceEventKey)
            || metricDeltas.Count == 0)
        {
            return Array.Empty<AchievementEventRecord>();
        }

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (KeyValuePair<string, long> pair in metricDeltas)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0)
                {
                    continue;
                }

                string metricId = NormalizeId(pair.Key);
                string eventKey = NormalizeId(sourceEventKey);
                string metricEventKey = MetricEventRecordKey(userId, colonyId, metricId, eventKey);
                if (metricEventsByKey.ContainsKey(metricEventKey))
                {
                    continue;
                }

                string snapshotId = string.IsNullOrWhiteSpace(sourceSnapshotId) ? eventKey : sourceSnapshotId!.Trim();
                var metricEvent = new AchievementMetricEventRecord(
                    userId,
                    colonyId,
                    metricId,
                    eventKey,
                    Math.Max(0, pair.Value),
                    snapshotId,
                    recordedAtUtc);
                metricEventsByKey[metricEventKey] = metricEvent;

                string aggregateKey = MetricAggregateKey(userId, colonyId, metricId);
                if (metricAggregatesByKey.TryGetValue(aggregateKey, out AchievementMetricAggregateRecord? aggregate))
                {
                    aggregate = aggregate with
                    {
                        Value = aggregate.Value + metricEvent.Value,
                        SourceSnapshotId = snapshotId,
                        UpdatedAtUtc = recordedAtUtc
                    };
                }
                else
                {
                    aggregate = new AchievementMetricAggregateRecord(
                        userId,
                        colonyId,
                        metricId,
                        metricEvent.Value,
                        snapshotId,
                        recordedAtUtc);
                }

                metricAggregatesByKey[aggregateKey] = aggregate;
                totals[metricId] = aggregate.Value;
            }

            if (totals.Count > 0)
            {
                SaveLocked();
            }
        }

        if (totals.Count == 0)
        {
            return Array.Empty<AchievementEventRecord>();
        }

        var recorded = new List<AchievementEventRecord>();
        foreach (AchievementDefinition definition in ListDefinitions(additionalDefinitions))
        {
            if (definition.TryBuildCandidate(totals, out SnapshotAchievementCandidateDto? candidate))
            {
                if (TryRecord(
                        userId,
                        colonyId,
                        string.IsNullOrWhiteSpace(sourceSnapshotId) ? sourceEventKey : sourceSnapshotId!,
                        candidate!,
                        recordedAtUtc,
                        out AchievementEventRecord? record))
                {
                    recorded.Add(record!);
                }
            }
        }

        return recorded;
    }

    public IReadOnlyList<AchievementDefinition> ListDefinitions()
    {
        lock (definitionsGate)
        {
            return definitions.ToArray();
        }
    }

    public IReadOnlyList<AchievementDefinition> ListDefinitions(IEnumerable<AchievementDefinition>? additionalDefinitions)
    {
        if (additionalDefinitions is null)
        {
            return ListDefinitions();
        }

        var merged = new Dictionary<string, AchievementDefinition>(StringComparer.Ordinal);
        foreach (AchievementDefinition definition in ListDefinitions())
        {
            merged[definition.AchievementId] = definition;
        }

        foreach (AchievementDefinition definition in additionalDefinitions)
        {
            if (definition.IsValid)
            {
                merged[definition.AchievementId] = definition;
            }
        }

        return merged.Values.ToArray();
    }

    public bool Record(
        string userId,
        string colonyId,
        string sourceSnapshotId,
        SnapshotAchievementCandidateDto candidate,
        DateTimeOffset recordedAtUtc)
    {
        return TryRecord(userId, colonyId, sourceSnapshotId, candidate, recordedAtUtc, out _);
    }

    public bool TryRecord(
        string userId,
        string colonyId,
        string sourceSnapshotId,
        SnapshotAchievementCandidateDto candidate,
        DateTimeOffset recordedAtUtc,
        out AchievementEventRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(sourceSnapshotId)
            || string.IsNullOrWhiteSpace(candidate.AchievementId)
            || string.IsNullOrWhiteSpace(candidate.EventKey))
        {
            return false;
        }

        string achievementId = NormalizeId(candidate.AchievementId);
        string eventKey = NormalizeId(candidate.EventKey);
        string eventRecordKey = EventRecordKey(userId, colonyId, achievementId, eventKey);
        string aggregateKey = AggregateKey(userId, colonyId, achievementId);
        string category = string.IsNullOrWhiteSpace(candidate.Category)
            ? CategorySnapshot
            : candidate.Category!.Trim();
        string labelKey = string.IsNullOrWhiteSpace(candidate.LabelKey)
            ? "ClashOfRim.Achievement." + achievementId
            : candidate.LabelKey!.Trim();
        string? iconId = string.IsNullOrWhiteSpace(candidate.IconId) ? null : candidate.IconId!.Trim();
        string color = AchievementColors.Normalize(candidate.Color);
        string aggregationKind = NormalizeAggregation(candidate.AggregationKind);
        long value = Math.Max(0, candidate.Value);
        var eventRecord = new AchievementEventRecord(
            userId,
            colonyId,
            achievementId,
            eventKey,
            value,
            category,
            labelKey,
            iconId,
            aggregationKind,
            sourceSnapshotId,
            recordedAtUtc,
            candidate.MetadataJson)
        {
            Color = color
        };

        lock (gate)
        {
            if (eventsByKey.ContainsKey(eventRecordKey))
            {
                if (string.Equals(aggregationKind, AggregationMax, StringComparison.Ordinal)
                    && aggregatesByKey.TryGetValue(aggregateKey, out AchievementAggregateRecord? existingAggregate)
                    && value > existingAggregate.Value)
                {
                    aggregatesByKey[aggregateKey] = existingAggregate with
                    {
                        Value = value,
                        Category = category,
                        LabelKey = labelKey,
                        IconId = iconId ?? existingAggregate.IconId,
                        Color = color,
                        AggregationKind = aggregationKind,
                        SourceSnapshotId = sourceSnapshotId,
                        UpdatedAtUtc = recordedAtUtc
                    };
                    sortedAggregatesCache = null;
                    SaveLocked();
                }

                return false;
            }

            eventsByKey[eventRecordKey] = eventRecord;

            if (aggregatesByKey.TryGetValue(aggregateKey, out AchievementAggregateRecord? current))
            {
                long nextValue = string.Equals(aggregationKind, AggregationSum, StringComparison.Ordinal)
                    ? current.Value + value
                    : Math.Max(current.Value, value);
                aggregatesByKey[aggregateKey] = current with
                {
                    Value = nextValue,
                    Category = category,
                    LabelKey = labelKey,
                    IconId = iconId ?? current.IconId,
                    Color = color,
                    AggregationKind = aggregationKind,
                    SourceSnapshotId = sourceSnapshotId,
                    UpdatedAtUtc = recordedAtUtc
                };
            }
            else
            {
                aggregatesByKey[aggregateKey] = new AchievementAggregateRecord(
                    userId,
                    colonyId,
                    achievementId,
                    category,
                    labelKey,
                    iconId,
                    aggregationKind,
                    value,
                    sourceSnapshotId,
                    recordedAtUtc)
                {
                    Color = color
                };
            }

            sortedAggregatesCache = null;
            SaveLocked();
            record = eventRecord;
            return true;
        }
    }

    public IReadOnlyList<AchievementAggregateRecord> ListAggregates()
    {
        lock (gate)
        {
            sortedAggregatesCache ??= aggregatesByKey.Values
                .Where(record => !IsDeprecatedAchievementId(record.AchievementId))
                .OrderBy(record => record.Category, StringComparer.Ordinal)
                .ThenBy(record => record.AchievementId, StringComparer.Ordinal)
                .ThenByDescending(record => record.Value)
                .ThenBy(record => record.UserId, StringComparer.Ordinal)
                .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
                .ToArray();
            return sortedAggregatesCache;
        }
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
            AchievementRegistryPersistence? persisted =
                JsonSerializer.Deserialize<AchievementRegistryPersistence>(json, JsonOptions);
            if (persisted?.Events is not null)
            {
                foreach (AchievementEventRecord record in persisted.Events)
                {
                    if (string.IsNullOrWhiteSpace(record.UserId)
                        || string.IsNullOrWhiteSpace(record.ColonyId)
                        || string.IsNullOrWhiteSpace(record.AchievementId)
                        || string.IsNullOrWhiteSpace(record.EventKey)
                        || IsDeprecatedAchievementId(record.AchievementId))
                    {
                        continue;
                    }

                    eventsByKey[EventRecordKey(record.UserId, record.ColonyId, record.AchievementId, record.EventKey)] = record;
                }
            }

            if (persisted?.Aggregates is not null)
            {
                foreach (AchievementAggregateRecord record in persisted.Aggregates)
                {
                    if (string.IsNullOrWhiteSpace(record.UserId)
                        || string.IsNullOrWhiteSpace(record.ColonyId)
                        || string.IsNullOrWhiteSpace(record.AchievementId)
                        || IsDeprecatedAchievementId(record.AchievementId))
                    {
                        continue;
                    }

                    aggregatesByKey[AggregateKey(record.UserId, record.ColonyId, record.AchievementId)] = record;
                }
            }

            if (persisted?.MetricEvents is not null)
            {
                foreach (AchievementMetricEventRecord record in persisted.MetricEvents)
                {
                    if (string.IsNullOrWhiteSpace(record.UserId)
                        || string.IsNullOrWhiteSpace(record.ColonyId)
                        || string.IsNullOrWhiteSpace(record.MetricId)
                        || string.IsNullOrWhiteSpace(record.EventKey))
                    {
                        continue;
                    }

                    metricEventsByKey[MetricEventRecordKey(record.UserId, record.ColonyId, record.MetricId, record.EventKey)] = record;
                }
            }

            if (persisted?.MetricAggregates is not null)
            {
                foreach (AchievementMetricAggregateRecord record in persisted.MetricAggregates)
                {
                    if (string.IsNullOrWhiteSpace(record.UserId)
                        || string.IsNullOrWhiteSpace(record.ColonyId)
                        || string.IsNullOrWhiteSpace(record.MetricId))
                    {
                        continue;
                    }

                    metricAggregatesByKey[MetricAggregateKey(record.UserId, record.ColonyId, record.MetricId)] = record;
                }
            }
        }
        catch (JsonException)
        {
            eventsByKey.Clear();
            aggregatesByKey.Clear();
            metricEventsByKey.Clear();
            metricAggregatesByKey.Clear();
            sortedAggregatesCache = null;
        }
    }

    private void SaveLocked()
    {
        if (persistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new AchievementRegistryPersistence(
                eventsByKey.Values
                    .OrderBy(record => record.RecordedAtUtc)
                    .ThenBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.AchievementId, StringComparer.Ordinal)
                    .ThenBy(record => record.EventKey, StringComparer.Ordinal)
                    .ToArray(),
                ListAggregatesLocked(),
                metricEventsByKey.Values
                    .OrderBy(record => record.RecordedAtUtc)
                    .ThenBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.MetricId, StringComparer.Ordinal)
                    .ThenBy(record => record.EventKey, StringComparer.Ordinal)
                    .ToArray(),
                metricAggregatesByKey.Values
                    .OrderBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
                    .ThenBy(record => record.MetricId, StringComparer.Ordinal)
                    .ToArray()),
            JsonOptions);
        persistence.Write(json);
    }

    private IReadOnlyList<AchievementAggregateRecord> ListAggregatesLocked()
    {
        return aggregatesByKey.Values
            .Where(record => !IsDeprecatedAchievementId(record.AchievementId))
            .OrderBy(record => record.Category, StringComparer.Ordinal)
            .ThenBy(record => record.AchievementId, StringComparer.Ordinal)
            .ThenBy(record => record.UserId, StringComparer.Ordinal)
            .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeAggregation(string? value)
    {
        return string.Equals(value, AggregationSum, StringComparison.OrdinalIgnoreCase)
            ? AggregationSum
            : AggregationMax;
    }

    private static string NormalizeId(string value)
    {
        return value.Trim();
    }

    private static bool IsDeprecatedAchievementId(string achievementId)
    {
        return string.Equals(achievementId, "wealth_peak", StringComparison.Ordinal);
    }

    private static string EventRecordKey(string userId, string colonyId, string achievementId, string eventKey)
    {
        return userId + "\n" + colonyId + "\n" + achievementId + "\n" + eventKey;
    }

    private static string AggregateKey(string userId, string colonyId, string achievementId)
    {
        return userId + "\n" + colonyId + "\n" + achievementId;
    }

    private static string MetricEventRecordKey(string userId, string colonyId, string metricId, string eventKey)
    {
        return userId + "\n" + colonyId + "\n" + metricId + "\n" + eventKey;
    }

    private static string MetricAggregateKey(string userId, string colonyId, string metricId)
    {
        return userId + "\n" + colonyId + "\n" + metricId;
    }

    private sealed record AchievementRegistryPersistence(
        IReadOnlyList<AchievementEventRecord> Events,
        IReadOnlyList<AchievementAggregateRecord> Aggregates,
        IReadOnlyList<AchievementMetricEventRecord>? MetricEvents = null,
        IReadOnlyList<AchievementMetricAggregateRecord>? MetricAggregates = null);

}

public sealed record AchievementDefinition(
    string AchievementId,
    string MetricId,
    long Threshold,
    string Category,
    string LabelKey,
    string? IconId,
    string AggregationKind)
{
    public string Color { get; init; } = AchievementColors.Green;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(AchievementId)
        && !string.IsNullOrWhiteSpace(MetricId)
        && Threshold >= 0
        && !string.IsNullOrWhiteSpace(Category)
        && !string.IsNullOrWhiteSpace(LabelKey);

    public static AchievementDefinition NumericThreshold(
        string achievementId,
        string metricId,
        long threshold,
        string category,
        string labelKey,
        string? iconId,
        string? color = null)
    {
        return new AchievementDefinition(
            achievementId,
            metricId,
            threshold,
            category,
            labelKey,
            iconId,
            AchievementRegistry.AggregationMax)
        {
            Color = AchievementColors.Normalize(color)
        };
    }

    public bool TryBuildCandidate(
        IReadOnlyDictionary<string, long> metrics,
        out SnapshotAchievementCandidateDto? candidate)
    {
        candidate = null;
        if (!metrics.TryGetValue(MetricId, out long value) || value < Threshold)
        {
            return false;
        }

        candidate = new SnapshotAchievementCandidateDto(
            AchievementId,
            "threshold:" + MetricId + ":" + Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value,
            Category,
            LabelKey,
            IconId,
            Color,
            AggregationKind);
        return true;
    }
}

public sealed record AchievementEventRecord(
    string UserId,
    string ColonyId,
    string AchievementId,
    string EventKey,
    long Value,
    string Category,
    string LabelKey,
    string? IconId,
    string AggregationKind,
    string SourceSnapshotId,
    DateTimeOffset RecordedAtUtc,
    string? MetadataJson)
{
    public string Color { get; init; } = AchievementColors.Green;
}

public sealed record AchievementAggregateRecord(
    string UserId,
    string ColonyId,
    string AchievementId,
    string Category,
    string LabelKey,
    string? IconId,
    string AggregationKind,
    long Value,
    string? SourceSnapshotId,
    DateTimeOffset UpdatedAtUtc)
{
    public string Color { get; init; } = AchievementColors.Green;
}

public sealed record AchievementMetricEventRecord(
    string UserId,
    string ColonyId,
    string MetricId,
    string EventKey,
    long Value,
    string? SourceSnapshotId,
    DateTimeOffset RecordedAtUtc);

public sealed record AchievementMetricAggregateRecord(
    string UserId,
    string ColonyId,
    string MetricId,
    long Value,
    string? SourceSnapshotId,
    DateTimeOffset UpdatedAtUtc);
