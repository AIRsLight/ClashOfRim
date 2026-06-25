using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Network;

public sealed class AdminBaselineRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private AdminBaselineSnapshot? baseline;
    private AdminBaselineSnapshot? cachedEffectiveTradeFeeBaseline;
    private TradeFeePolicy? cachedEffectiveTradeFeeConfiguredPolicy;
    private TradeFeePolicy? cachedEffectiveTradeFeePolicy;

    public AdminBaselineRegistry(string? persistencePath = null)
        : this(string.IsNullOrWhiteSpace(persistencePath) ? null : new FileJsonPersistenceSlot(persistencePath))
    {
    }

    internal AdminBaselineRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public AdminBaselineSnapshot? Current
    {
        get
        {
            lock (gate)
            {
                return baseline;
            }
        }
    }

    public AdminBaselineSnapshot Submit(SubmitAdminBaselineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AdminBaselineSnapshot normalized = AdminBaselineSnapshot.FromRequest(request);
        lock (gate)
        {
            baseline = normalized;
            ClearEffectiveTradeFeePolicyCacheLocked();
            SaveLocked();
            return normalized;
        }
    }

    public TradeFeePolicy BuildEffectiveTradeFeePolicy(ClashOfRimServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        TradeFeePolicy configuredPolicy = configuration.TradeFeePolicy;
        lock (gate)
        {
            AdminBaselineSnapshot? snapshot = baseline;
            if (snapshot is null || snapshot.StandardMarketValuePerThing.Count == 0)
            {
                return configuredPolicy;
            }

            if (cachedEffectiveTradeFeePolicy is not null
                && ReferenceEquals(cachedEffectiveTradeFeeBaseline, snapshot)
                && ReferenceEquals(cachedEffectiveTradeFeeConfiguredPolicy, configuredPolicy))
            {
                return cachedEffectiveTradeFeePolicy;
            }

            TradeFeePolicy effectivePolicy = new(
                configuredPolicy.BaseFeeRate,
                configuredPolicy.FixedFeePerThing,
                snapshot.StandardMarketValuePerThing,
                snapshot.StuffMarketValueByThingAndStuff,
                snapshot.QualityMarketValueModifierByQuality,
                snapshot.WeaponTraitMarketValueOffsetByDefName,
                configuredPolicy.ItemHealthValueCurve,
                configuredPolicy.RepairableBuildingHealthValueCurve,
                configuredPolicy.FeeStrategy);
            cachedEffectiveTradeFeeBaseline = snapshot;
            cachedEffectiveTradeFeeConfiguredPolicy = configuredPolicy;
            cachedEffectiveTradeFeePolicy = effectivePolicy;
            return effectivePolicy;
        }
    }

    private void ClearEffectiveTradeFeePolicyCacheLocked()
    {
        cachedEffectiveTradeFeeBaseline = null;
        cachedEffectiveTradeFeeConfiguredPolicy = null;
        cachedEffectiveTradeFeePolicy = null;
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

            baseline = JsonSerializer.Deserialize<AdminBaselineSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            baseline = null;
        }
        catch (IOException)
        {
            baseline = null;
        }
    }

    private void SaveLocked()
    {
        if (persistence is null || baseline is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(baseline, JsonOptions);
        persistence.Write(json);
    }
}

public sealed class AdminBaselineSnapshot
{
    private static string MarketValueKey(string thingDefName, string stuffDefName)
    {
        return thingDefName.Trim() + "|" + stuffDefName.Trim();
    }

    public AdminBaselineSnapshot(
        string userId,
        string colonyId,
        DateTimeOffset generatedAtUtc,
        IReadOnlyDictionary<string, float> standardMarketValuePerThing,
        IReadOnlyList<TrapClassificationDto> trapClassifications,
        IReadOnlyList<PackableBuildingDto>? packableBuildings = null,
        IReadOnlyList<BuildingBaselineDto>? buildings = null,
        IReadOnlyList<AdminBaselineExtensionDto>? baselineExtensions = null,
        IReadOnlyList<StuffHitPointModifierDto>? stuffHitPointModifiers = null,
        IReadOnlyList<StuffMarketValueDto>? stuffMarketValues = null,
        IReadOnlyList<QualityMarketValueModifierDto>? qualityMarketValueModifiers = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        GeneratedAtUtc = generatedAtUtc;
        StandardMarketValuePerThing = standardMarketValuePerThing;
        TrapClassifications = trapClassifications;
        ApprovedTrapDefNames = new HashSet<string>(
            TrapClassifications
                .Where(entry => entry.IncludedInApprovedManifest)
                .Select(entry => entry.DefName),
            StringComparer.OrdinalIgnoreCase);
        PackableBuildings = NormalizeLastByKey(
            packableBuildings,
            entry => entry != null && !string.IsNullOrWhiteSpace(entry.DefName),
            entry => entry.DefName,
            entry => entry.ModPackageId,
            entry => entry.DefName);
        PackableBuildingDefNames = new HashSet<string>(
            PackableBuildings.Select(entry => entry.DefName),
            StringComparer.OrdinalIgnoreCase);
        Buildings = NormalizeLastByKey(
            buildings,
            entry => entry != null && !string.IsNullOrWhiteSpace(entry.DefName),
            entry => entry.DefName,
            entry => entry.ModPackageId,
            entry => entry.DefName);
        EstimatedBuildingMaxHitPointsByDefName = BuildLastValueByKey(
            Buildings,
            entry => entry.UseHitPoints && entry.EstimatedMaxHitPoints > 0,
            entry => entry.DefName,
            entry => Math.Max(1, entry.EstimatedMaxHitPoints));
        BaselineExtensions = NormalizeLastByKey(
            baselineExtensions,
            entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.ProviderId)
                && !string.IsNullOrWhiteSpace(entry.Kind),
            entry => entry.ProviderId.Trim() + "|" + entry.Kind.Trim(),
            entry => entry.ProviderId,
            entry => entry.Kind);
        EstimatedExtensionMaxHitPointsByDefName = BuildLastValueByKey(
            ReadExtensionHitPointBaselines(BaselineExtensions),
            entry => true,
            entry => entry.Key,
            entry => Math.Max(1, entry.Value));
        EstimatedSettlementMaxHitPointsByDefName = MergeMaxHitPointBaselines(
            EstimatedBuildingMaxHitPointsByDefName,
            EstimatedExtensionMaxHitPointsByDefName);
        StuffHitPointModifiers = NormalizeLastByKey(
            stuffHitPointModifiers,
            entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.DefName)
                && (entry.MaxHitPointsFactor != 1f || entry.MaxHitPointsOffset != 0f),
            entry => entry.DefName,
            entry => entry.ModPackageId,
            entry => entry.DefName);
        StuffHitPointFactorByDefName = BuildLastValueByKey(
            StuffHitPointModifiers,
            entry => entry.MaxHitPointsFactor != 1f,
            entry => entry.DefName,
            entry => entry.MaxHitPointsFactor);
        StuffHitPointOffsetByDefName = BuildLastValueByKey(
            StuffHitPointModifiers,
            entry => entry.MaxHitPointsOffset != 0f,
            entry => entry.DefName,
            entry => entry.MaxHitPointsOffset);
        StuffMarketValues = NormalizeLastByKey(
            stuffMarketValues,
            entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.ThingDefName)
                && !string.IsNullOrWhiteSpace(entry.StuffDefName)
                && entry.MarketValue >= 0f,
            entry => MarketValueKey(entry.ThingDefName, entry.StuffDefName),
            entry => entry.ThingDefName,
            entry => entry.StuffDefName);
        StuffMarketValueByThingAndStuff = BuildLastValueByKey(
            StuffMarketValues,
            entry => true,
            entry => MarketValueKey(entry.ThingDefName, entry.StuffDefName),
            entry => Math.Max(0f, entry.MarketValue));
        QualityMarketValueModifiers = NormalizeLastByKey(
            qualityMarketValueModifiers,
            entry => entry != null && !string.IsNullOrWhiteSpace(entry.Quality),
            entry => entry.Quality,
            entry => entry.Quality,
            static _ => string.Empty);
        QualityMarketValueModifierByQuality = BuildLastValueByKey(
            QualityMarketValueModifiers,
            entry => true,
            entry => entry.Quality,
            entry => new TradeQualityValueModifier(Math.Max(0f, entry.Factor), Math.Max(0f, entry.MaxGain)));
        WeaponTraitMarketValueOffsetByDefName = BuildLastValueByKey(
            ReadWeaponTraitMarketValueOffsets(BaselineExtensions),
            entry => true,
            entry => entry.Key,
            entry => entry.Value);
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyDictionary<string, float> StandardMarketValuePerThing { get; }

    public IReadOnlyList<TrapClassificationDto> TrapClassifications { get; }

    public IReadOnlySet<string> ApprovedTrapDefNames { get; }

    public IReadOnlyList<PackableBuildingDto> PackableBuildings { get; }

    public IReadOnlySet<string> PackableBuildingDefNames { get; }

    public IReadOnlyList<BuildingBaselineDto> Buildings { get; }

    public IReadOnlyDictionary<string, int> EstimatedBuildingMaxHitPointsByDefName { get; }

    public IReadOnlyList<AdminBaselineExtensionDto> BaselineExtensions { get; }

    public IReadOnlyDictionary<string, int> EstimatedExtensionMaxHitPointsByDefName { get; }

    public IReadOnlyDictionary<string, int> EstimatedSettlementMaxHitPointsByDefName { get; }

    public IReadOnlyList<StuffHitPointModifierDto> StuffHitPointModifiers { get; }

    public IReadOnlyDictionary<string, float> StuffHitPointFactorByDefName { get; }

    public IReadOnlyDictionary<string, float> StuffHitPointOffsetByDefName { get; }

    public IReadOnlyList<StuffMarketValueDto> StuffMarketValues { get; }

    public IReadOnlyDictionary<string, float> StuffMarketValueByThingAndStuff { get; }

    public IReadOnlyList<QualityMarketValueModifierDto> QualityMarketValueModifiers { get; }

    public IReadOnlyDictionary<string, TradeQualityValueModifier> QualityMarketValueModifierByQuality { get; }

    public IReadOnlyDictionary<string, float> WeaponTraitMarketValueOffsetByDefName { get; }

    public int TrapAutoApprovedCount => TrapClassifications.Count(entry => entry.InheritsBuildingTrap);

    public int TrapCandidateCount => TrapClassifications.Count(entry => !entry.InheritsBuildingTrap);

    public int TrapApprovedCount => TrapClassifications.Count(entry => entry.IncludedInApprovedManifest);

    public int PackableBuildingCount => PackableBuildings.Count;

    public int BuildingCount => Buildings.Count;

    public int BaselineExtensionCount => BaselineExtensions.Count;

    public bool IsApprovedPackableBuilding(string? defName)
    {
        return !string.IsNullOrWhiteSpace(defName)
            && PackableBuildingDefNames.Contains(defName);
    }

    public static AdminBaselineSnapshot FromRequest(SubmitAdminBaselineRequest request)
    {
        return new AdminBaselineSnapshot(
            request.UserId,
            request.ColonyId ?? string.Empty,
            request.GeneratedAtUtc,
            BuildLastValueByKey(
                request.StandardMarketValues,
                entry => entry != null && !string.IsNullOrWhiteSpace(entry.DefName) && entry.MarketValue >= 0f,
                entry => entry.DefName,
                entry => Math.Max(0f, entry.MarketValue)),
            NormalizeLastByKey(
                request.TrapClassifications,
                entry => entry != null && !string.IsNullOrWhiteSpace(entry.DefName),
                entry => entry.DefName,
                entry => entry.ModPackageId,
                entry => entry.DefName),
            request.PackableBuildings,
            request.Buildings,
            request.BaselineExtensions,
            request.StuffHitPointModifiers,
            request.StuffMarketValues,
            request.QualityMarketValueModifiers);
    }

    private static IReadOnlyList<T> NormalizeLastByKey<T>(
        IEnumerable<T>? source,
        Func<T, bool> isValid,
        Func<T, string> keySelector,
        Func<T, string?> primarySortSelector,
        Func<T, string?> secondarySortSelector)
    {
        var byKey = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (T entry in source)
            {
                if (!isValid(entry))
                {
                    continue;
                }

                string key = keySelector(entry);
                byKey[key] = entry;
            }
        }

        var result = new List<T>(byKey.Values);
        result.Sort((left, right) =>
        {
            int primary = string.Compare(primarySortSelector(left), primarySortSelector(right), StringComparison.Ordinal);
            return primary != 0
                ? primary
                : string.Compare(secondarySortSelector(left), secondarySortSelector(right), StringComparison.Ordinal);
        });
        return result;
    }

    private static Dictionary<string, TValue> BuildLastValueByKey<T, TValue>(
        IEnumerable<T>? source,
        Func<T, bool> isValid,
        Func<T, string> keySelector,
        Func<T, TValue> valueSelector)
    {
        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (T entry in source)
        {
            if (!isValid(entry))
            {
                continue;
            }

            result[keySelector(entry)] = valueSelector(entry);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, int> MergeMaxHitPointBaselines(
        IReadOnlyDictionary<string, int> buildings,
        IReadOnlyDictionary<string, int> extensions)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> entry in buildings)
        {
            result[entry.Key] = Math.Max(1, entry.Value);
        }

        foreach (KeyValuePair<string, int> entry in extensions)
        {
            result[entry.Key] = Math.Max(1, entry.Value);
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, int>> ReadExtensionHitPointBaselines(
        IEnumerable<AdminBaselineExtensionDto> extensions)
    {
        foreach (AdminBaselineExtensionDto extension in extensions)
        {
            if (!string.Equals(extension.Kind, "hitPointBaseline", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (AdminBaselineExtensionRecordDto record in extension.Records ?? Array.Empty<AdminBaselineExtensionRecordDto>())
            {
                if (record.Values is null)
                {
                    continue;
                }

                string defName = ReadValue(record, "defName");
                if (string.IsNullOrWhiteSpace(defName))
                {
                    defName = record.Key;
                }

                if (string.IsNullOrWhiteSpace(defName)
                    || !ReadBool(record, "useHitPoints")
                    || !int.TryParse(ReadValue(record, "estimatedMaxHitPoints"), out int estimatedMaxHitPoints)
                    || estimatedMaxHitPoints <= 0)
                {
                    continue;
                }

                yield return new KeyValuePair<string, int>(defName, Math.Max(1, estimatedMaxHitPoints));
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, float>> ReadWeaponTraitMarketValueOffsets(
        IEnumerable<AdminBaselineExtensionDto> extensions)
    {
        foreach (AdminBaselineExtensionDto extension in extensions)
        {
            if (!string.Equals(extension.Kind, "weaponTraitMarketValueBaseline", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (AdminBaselineExtensionRecordDto record in extension.Records ?? Array.Empty<AdminBaselineExtensionRecordDto>())
            {
                if (record.Values is null)
                {
                    continue;
                }

                string defName = ReadValue(record, "defName");
                if (string.IsNullOrWhiteSpace(defName))
                {
                    defName = record.Key;
                }

                if (string.IsNullOrWhiteSpace(defName)
                    || !float.TryParse(ReadValue(record, "marketValueOffset"), out float marketValueOffset))
                {
                    continue;
                }

                yield return new KeyValuePair<string, float>(defName, marketValueOffset);
            }
        }
    }

    private static string ReadValue(AdminBaselineExtensionRecordDto record, string key)
    {
        return record.Values.TryGetValue(key, out string? value)
            ? value ?? string.Empty
            : string.Empty;
    }

    private static bool ReadBool(AdminBaselineExtensionRecordDto record, string key)
    {
        return bool.TryParse(ReadValue(record, key), out bool value) && value;
    }
}
