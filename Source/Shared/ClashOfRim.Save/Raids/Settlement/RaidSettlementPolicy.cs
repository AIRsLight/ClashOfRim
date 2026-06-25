namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidSettlementPolicy
{
    public const double DefaultBuildingHitPointsLossRatio = 0.9;

    public static RaidSettlementPolicy FullLoss { get; } = new(
        1.0,
        buildingHitPointsLossRatio: 1.0);

    public RaidSettlementPolicy(
        double lossRatio,
        string? eventId = null,
        IEnumerable<string>? packableBuildingDefNames = null,
        IReadOnlyDictionary<string, int>? buildingMaxHitPointsByDefName = null,
        IReadOnlyDictionary<string, float>? stuffHitPointFactorByDefName = null,
        IReadOnlyDictionary<string, float>? stuffHitPointOffsetByDefName = null,
        double minimumRemainingHitPointsRatio = 0,
        IEnumerable<string>? ignoredThingDefNames = null,
        double buildingHitPointsLossRatio = DefaultBuildingHitPointsLossRatio,
        IEnumerable<string>? trapDefNames = null)
    {
        if (double.IsNaN(lossRatio) || lossRatio < 0 || lossRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LossRatio), "Loss ratio must be between 0 and 1.");
        }
        if (double.IsNaN(buildingHitPointsLossRatio)
            || buildingHitPointsLossRatio < 0
            || buildingHitPointsLossRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BuildingHitPointsLossRatio), "Building hit points loss ratio must be between 0 and 1.");
        }
        if (double.IsNaN(minimumRemainingHitPointsRatio)
            || minimumRemainingHitPointsRatio < 0
            || minimumRemainingHitPointsRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRemainingHitPointsRatio), "Minimum remaining hit points ratio must be between 0 and 1.");
        }

        LossRatio = lossRatio;
        EventId = string.IsNullOrWhiteSpace(eventId) ? "unspecified" : eventId;
        PackableBuildingDefNames = new HashSet<string>(
            packableBuildingDefNames?.Where(defName => !string.IsNullOrWhiteSpace(defName)) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        BuildingMaxHitPointsByDefName = new Dictionary<string, int>(
            buildingMaxHitPointsByDefName?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
                .Select(entry => new KeyValuePair<string, int>(entry.Key, Math.Max(1, entry.Value)))
                ?? Array.Empty<KeyValuePair<string, int>>(),
            StringComparer.OrdinalIgnoreCase);
        StuffHitPointFactorByDefName = new Dictionary<string, float>(
            stuffHitPointFactorByDefName?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !float.IsNaN(entry.Value) && entry.Value > 0)
                .Select(entry => new KeyValuePair<string, float>(entry.Key, entry.Value))
                ?? Array.Empty<KeyValuePair<string, float>>(),
            StringComparer.OrdinalIgnoreCase);
        StuffHitPointOffsetByDefName = new Dictionary<string, float>(
            stuffHitPointOffsetByDefName?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !float.IsNaN(entry.Value))
                .Select(entry => new KeyValuePair<string, float>(entry.Key, entry.Value))
                ?? Array.Empty<KeyValuePair<string, float>>(),
            StringComparer.OrdinalIgnoreCase);
        BuildingHitPointsLossRatio = buildingHitPointsLossRatio;
        MinimumRemainingHitPointsRatio = minimumRemainingHitPointsRatio;
        IgnoredThingDefNames = new HashSet<string>(
            ignoredThingDefNames?.Where(defName => !string.IsNullOrWhiteSpace(defName)) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        TrapDefNames = new HashSet<string>(
            trapDefNames?.Where(defName => !string.IsNullOrWhiteSpace(defName)) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public double LossRatio { get; }

    public double BuildingHitPointsLossRatio { get; }

    public string EventId { get; }

    public IReadOnlySet<string> PackableBuildingDefNames { get; }

    public IReadOnlyDictionary<string, int> BuildingMaxHitPointsByDefName { get; }

    public IReadOnlyDictionary<string, float> StuffHitPointFactorByDefName { get; }

    public IReadOnlyDictionary<string, float> StuffHitPointOffsetByDefName { get; }

    public double MinimumRemainingHitPointsRatio { get; }

    public IReadOnlySet<string> IgnoredThingDefNames { get; }

    public IReadOnlySet<string> TrapDefNames { get; }

    public bool IsPackableBuilding(string? defName)
    {
        return !string.IsNullOrWhiteSpace(defName)
            && PackableBuildingDefNames.Contains(defName);
    }

    public bool IsKnownBuilding(string? defName)
    {
        return !string.IsNullOrWhiteSpace(defName)
            && BuildingMaxHitPointsByDefName.ContainsKey(defName);
    }

    public bool IsTrap(string? defName)
    {
        return !string.IsNullOrWhiteSpace(defName)
            && TrapDefNames.Contains(defName);
    }

    public int? EstimatedMaxHitPoints(string? defName, string? stuffDefName)
    {
        int? baseMaxHitPoints = !string.IsNullOrWhiteSpace(defName)
            && BuildingMaxHitPointsByDefName.TryGetValue(defName, out int maxHitPoints)
            ? maxHitPoints
            : null;
        if (!baseMaxHitPoints.HasValue)
        {
            return null;
        }

        float factor = !string.IsNullOrWhiteSpace(stuffDefName)
            && StuffHitPointFactorByDefName.TryGetValue(stuffDefName!, out float foundFactor)
            ? foundFactor
            : 1f;
        float offset = !string.IsNullOrWhiteSpace(stuffDefName)
            && StuffHitPointOffsetByDefName.TryGetValue(stuffDefName!, out float foundOffset)
            ? foundOffset
            : 0f;
        return Math.Max(1, (int)Math.Round(baseMaxHitPoints.Value * factor + offset, MidpointRounding.AwayFromZero));
    }
}
