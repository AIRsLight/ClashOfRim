using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

public static class TradeFeeStrategies
{
    public const string Publisher = "Publisher";
    public const string HighestSide = "HighestSide";
    public const string SumBothSides = "SumBothSides";

    public static string Normalize(string? strategy)
    {
        if (string.Equals(strategy, HighestSide, StringComparison.OrdinalIgnoreCase))
        {
            return HighestSide;
        }

        if (string.Equals(strategy, SumBothSides, StringComparison.OrdinalIgnoreCase))
        {
            return SumBothSides;
        }

        return Publisher;
    }
}

public sealed class TradeFeePolicy
{
    public const float DefaultBaseFeeRate = 0.05f;

    public static TradeHealthValueCurve DefaultItemHealthValueCurve { get; } = new(new[]
    {
        new TradeHealthValuePoint(0f, 0f),
        new TradeHealthValuePoint(0.5f, 0.1f),
        new TradeHealthValuePoint(0.6f, 0.5f),
        new TradeHealthValuePoint(0.9f, 1f)
    });

    public static TradeHealthValueCurve DefaultRepairableBuildingHealthValueCurve { get; } = new(new[]
    {
        new TradeHealthValuePoint(0f, 0.5f),
        new TradeHealthValuePoint(0.5f, 0.75f),
        new TradeHealthValuePoint(0.9f, 1f)
    });

    private static readonly IReadOnlyDictionary<string, int> DefaultFixedFees =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Wastepack"] = 100
        };

    public TradeFeePolicy(
        float baseFeeRate = DefaultBaseFeeRate,
        IReadOnlyDictionary<string, int>? fixedFeePerThing = null,
        IReadOnlyDictionary<string, float>? standardMarketValuePerThing = null,
        IReadOnlyDictionary<string, float>? stuffMarketValuePerThingAndStuff = null,
        IReadOnlyDictionary<string, TradeQualityValueModifier>? qualityMarketValueModifiers = null,
        IReadOnlyDictionary<string, float>? weaponTraitMarketValueOffsets = null,
        TradeHealthValueCurve? itemHealthValueCurve = null,
        TradeHealthValueCurve? repairableBuildingHealthValueCurve = null,
        string? feeStrategy = null)
    {
        BaseFeeRate = Math.Max(0f, baseFeeRate);
        FeeStrategy = TradeFeeStrategies.Normalize(feeStrategy);
        FixedFeePerThing = (fixedFeePerThing ?? DefaultFixedFees)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        StandardMarketValuePerThing = (standardMarketValuePerThing ?? new Dictionary<string, float>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value >= 0f)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        StuffMarketValuePerThingAndStuff = (stuffMarketValuePerThingAndStuff ?? new Dictionary<string, float>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value >= 0f)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        QualityMarketValueModifiers = (qualityMarketValueModifiers ?? new Dictionary<string, TradeQualityValueModifier>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        WeaponTraitMarketValueOffsets = (weaponTraitMarketValueOffsets ?? new Dictionary<string, float>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        ItemHealthValueCurve = itemHealthValueCurve ?? DefaultItemHealthValueCurve;
        RepairableBuildingHealthValueCurve = repairableBuildingHealthValueCurve ?? DefaultRepairableBuildingHealthValueCurve;
    }

    public float BaseFeeRate { get; }

    public string FeeStrategy { get; }

    public IReadOnlyDictionary<string, int> FixedFeePerThing { get; }

    public IReadOnlyDictionary<string, float> StandardMarketValuePerThing { get; }

    public IReadOnlyDictionary<string, float> StuffMarketValuePerThingAndStuff { get; }

    public IReadOnlyDictionary<string, TradeQualityValueModifier> QualityMarketValueModifiers { get; }

    public IReadOnlyDictionary<string, float> WeaponTraitMarketValueOffsets { get; }

    public TradeHealthValueCurve ItemHealthValueCurve { get; }

    public TradeHealthValueCurve RepairableBuildingHealthValueCurve { get; }

    public static TradeFeePolicy Default { get; } = new();

    public int CalculateRequiredFee(IReadOnlyCollection<ThingReferenceDto> offeredThings)
    {
        return CalculateRequiredFeeResult(offeredThings).RequiredFeeSilver;
    }

    public TradeFeeCalculationResult CalculateRequiredFeeResult(IReadOnlyCollection<ThingReferenceDto> offeredThings)
    {
        return CalculateRequiredFeeResult(offeredThings, Array.Empty<ThingReferenceDto>());
    }

    public TradeFeeCalculationResult CalculateRequiredFeeResult(
        IReadOnlyCollection<ThingReferenceDto>? publisherThings,
        IReadOnlyCollection<ThingReferenceDto>? counterpartyThings)
    {
        TradeFeeCalculationResult publisher = CalculateSingleSideFeeResult(publisherThings ?? Array.Empty<ThingReferenceDto>());
        string strategy = TradeFeeStrategies.Normalize(FeeStrategy);
        if (strategy == TradeFeeStrategies.Publisher)
        {
            return publisher;
        }

        TradeFeeCalculationResult counterparty = CalculateSingleSideFeeResult(counterpartyThings ?? Array.Empty<ThingReferenceDto>());
        return strategy == TradeFeeStrategies.SumBothSides
            ? SumFeeResults(publisher, counterparty)
            : HighestFeeResult(publisher, counterparty);
    }

    private TradeFeeCalculationResult CalculateSingleSideFeeResult(IReadOnlyCollection<ThingReferenceDto> things)
    {
        float totalValue = 0f;
        int fixedFee = 0;
        var missingDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ThingReferenceDto thing in things)
        {
            string? defName = EffectiveFeeDefName(thing);
            if (string.IsNullOrWhiteSpace(defName))
            {
                continue;
            }

            string defKey = defName!;
            fixedFee += CalculateFixedFeeForThing(thing, defKey);

            float? unitValue = TryGetServerUnitValue(thing, defKey);
            if (unitValue is null)
            {
                if (BaseFeeRate > 0f)
                {
                    missingDefs.Add(defKey);
                }

                continue;
            }

            totalValue += Math.Max(0f, unitValue.Value) * Math.Max(1, thing.StackCount);
        }

        int baseFee = totalValue <= 0.01f
            ? 0
            : Math.Max(1, (int)Math.Ceiling(totalValue * BaseFeeRate));
        IReadOnlyList<string> missingMarketValueDefs = SortMissingMarketValueDefs(missingDefs);
        return new TradeFeeCalculationResult(
            missingMarketValueDefs.Count == 0,
            baseFee + fixedFee,
            baseFee,
            fixedFee,
            missingMarketValueDefs);
    }

    private static TradeFeeCalculationResult SumFeeResults(
        TradeFeeCalculationResult publisher,
        TradeFeeCalculationResult counterparty)
    {
        return new TradeFeeCalculationResult(
            publisher.Accepted && counterparty.Accepted,
            Math.Max(0, publisher.RequiredFeeSilver) + Math.Max(0, counterparty.RequiredFeeSilver),
            Math.Max(0, publisher.BaseFeeSilver) + Math.Max(0, counterparty.BaseFeeSilver),
            Math.Max(0, publisher.FixedFeeSilver) + Math.Max(0, counterparty.FixedFeeSilver),
            MergeMissingMarketValueDefs(publisher, counterparty));
    }

    private static TradeFeeCalculationResult HighestFeeResult(
        TradeFeeCalculationResult publisher,
        TradeFeeCalculationResult counterparty)
    {
        TradeFeeCalculationResult selected = counterparty.RequiredFeeSilver > publisher.RequiredFeeSilver
            ? counterparty
            : publisher;
        return new TradeFeeCalculationResult(
            publisher.Accepted && counterparty.Accepted,
            selected.RequiredFeeSilver,
            selected.BaseFeeSilver,
            selected.FixedFeeSilver,
            MergeMissingMarketValueDefs(publisher, counterparty));
    }

    private static IReadOnlyList<string> MergeMissingMarketValueDefs(
        TradeFeeCalculationResult publisher,
        TradeFeeCalculationResult counterparty)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in publisher.MissingMarketValueDefs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                missing.Add(value);
            }
        }

        foreach (string value in counterparty.MissingMarketValueDefs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                missing.Add(value);
            }
        }

        return SortMissingMarketValueDefs(missing);
    }

    public int CalculateFixedFee(IReadOnlyCollection<ThingReferenceDto> offeredThings)
    {
        int total = 0;
        foreach (ThingReferenceDto thing in offeredThings)
        {
            string? defName = EffectiveFeeDefName(thing);
            if (string.IsNullOrWhiteSpace(defName))
            {
                continue;
            }

            total += CalculateFixedFeeForThing(thing, defName!);
        }

        return total;
    }

    private int CalculateFixedFeeForThing(ThingReferenceDto thing, string defName)
    {
        if (!FixedFeePerThing.TryGetValue(defName, out int perThingFee)
            || perThingFee <= 0)
        {
            return 0;
        }

        return perThingFee * Math.Max(1, thing.StackCount);
    }

    private static IReadOnlyList<string> SortMissingMarketValueDefs(HashSet<string> missingDefs)
    {
        if (missingDefs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var sorted = new List<string>(missingDefs);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    private float? TryGetServerUnitValue(ThingReferenceDto thing, string defName)
    {
        if (IsPawnReference(thing))
        {
            return TryGetServerBaseUnitValue(thing, defName);
        }

        float? value = TryGetServerBaseUnitValue(thing, defName);
        if (value is null)
        {
            return null;
        }

        float qualityAdjusted = ApplyQualityModifier(value.Value, EffectiveQuality(thing));
        if (thing.UniqueWeapon == true && thing.UniqueWeaponTraits.Count > 0)
        {
            foreach (string traitDefName in thing.UniqueWeaponTraits)
            {
                if (!string.IsNullOrWhiteSpace(traitDefName)
                    && WeaponTraitMarketValueOffsets.TryGetValue(traitDefName, out float offset))
                {
                    qualityAdjusted += offset;
                }
            }

            qualityAdjusted = Math.Max(0f, qualityAdjusted);
        }

        if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName))
        {
            return ApplyHitPointsModifier(
                qualityAdjusted,
                EffectiveHitPoints(thing),
                EffectiveMaxHitPoints(thing),
                RepairableBuildingHealthValueCurve);
        }

        return ApplyHitPointsModifier(qualityAdjusted, EffectiveHitPoints(thing), EffectiveMaxHitPoints(thing), ItemHealthValueCurve);
    }

    private float? TryGetServerBaseUnitValue(ThingReferenceDto thing, string defName)
    {
        string? stuffDefName = EffectiveStuffDefName(thing);
        if (!string.IsNullOrWhiteSpace(stuffDefName)
            && StuffMarketValuePerThingAndStuff.TryGetValue(MarketValueKey(defName, stuffDefName!), out float stuffValue))
        {
            return stuffValue;
        }

        return StandardMarketValuePerThing.TryGetValue(defName, out float value)
            ? value
            : null;
    }

    private float ApplyQualityModifier(float value, string? quality)
    {
        if (value <= 0f
            || string.IsNullOrWhiteSpace(quality)
            || !QualityMarketValueModifiers.TryGetValue(quality!, out TradeQualityValueModifier modifier))
        {
            return value;
        }

        float gain = value * modifier.Factor - value;
        gain = Math.Min(gain, modifier.MaxGain);
        return Math.Max(0f, value + gain);
    }

    private static float ApplyHitPointsModifier(float value, int? hitPoints, int? maxHitPoints, TradeHealthValueCurve curve)
    {
        if (value <= 0f || hitPoints is null || maxHitPoints is null || maxHitPoints.Value <= 0)
        {
            return value;
        }

        float ratio = Clamp01((float)hitPoints.Value / maxHitPoints.Value);
        return Math.Max(0f, value * curve.Evaluate(ratio));
    }

    private static float Clamp01(float value)
    {
        if (value <= 0f)
        {
            return 0f;
        }

        return value >= 1f ? 1f : value;
    }

    private static string? EffectiveFeeDefName(ThingReferenceDto thing)
    {
        return string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
            ? thing.DefName
            : thing.MinifiedInnerDefName;
    }

    private static string? EffectiveStuffDefName(ThingReferenceDto thing)
    {
        return string.IsNullOrWhiteSpace(thing.MinifiedInnerStuffDefName)
            ? thing.StuffDefName
            : thing.MinifiedInnerStuffDefName;
    }

    private static string? EffectiveQuality(ThingReferenceDto thing)
    {
        return string.IsNullOrWhiteSpace(thing.MinifiedInnerQuality)
            ? thing.Quality
            : thing.MinifiedInnerQuality;
    }

    private static int? EffectiveHitPoints(ThingReferenceDto thing)
    {
        return thing.MinifiedInnerHitPoints ?? thing.HitPoints;
    }

    private static int? EffectiveMaxHitPoints(ThingReferenceDto thing)
    {
        return thing.MinifiedInnerMaxHitPoints ?? thing.MaxHitPoints;
    }

    private static bool IsPawnReference(ThingReferenceDto thing)
    {
        return thing.PawnPackage is not null
            || !string.IsNullOrWhiteSpace(thing.PawnPackageId);
    }

    private static string MarketValueKey(string thingDefName, string stuffDefName)
    {
        return thingDefName.Trim() + "|" + stuffDefName.Trim();
    }

}

public sealed class TradeQualityValueModifier
{
    public TradeQualityValueModifier(float factor, float maxGain)
    {
        Factor = Math.Max(0f, factor);
        MaxGain = Math.Max(0f, maxGain);
    }

    public float Factor { get; }

    public float MaxGain { get; }
}

public sealed class TradeHealthValueCurve
{
    public TradeHealthValueCurve(IReadOnlyList<TradeHealthValuePoint>? points)
    {
        Points = (points ?? Array.Empty<TradeHealthValuePoint>())
            .OrderBy(point => point.HealthRatio)
            .ToList();
    }

    public IReadOnlyList<TradeHealthValuePoint> Points { get; }

    public float Evaluate(float healthRatio)
    {
        if (Points.Count == 0)
        {
            return 1f;
        }

        float x = Clamp01(healthRatio);
        if (x <= Points[0].HealthRatio)
        {
            return Math.Max(0f, Points[0].ValueFactor);
        }

        for (int i = 1; i < Points.Count; i++)
        {
            TradeHealthValuePoint previous = Points[i - 1];
            TradeHealthValuePoint current = Points[i];
            if (x > current.HealthRatio)
            {
                continue;
            }

            float range = current.HealthRatio - previous.HealthRatio;
            if (range <= 0.0001f)
            {
                return Math.Max(0f, current.ValueFactor);
            }

            float t = (x - previous.HealthRatio) / range;
            return Math.Max(0f, previous.ValueFactor + (current.ValueFactor - previous.ValueFactor) * Clamp01(t));
        }

        return Math.Max(0f, Points[Points.Count - 1].ValueFactor);
    }

    private static float Clamp01(float value)
    {
        if (value <= 0f)
        {
            return 0f;
        }

        return value >= 1f ? 1f : value;
    }
}

public sealed class TradeHealthValuePoint
{
    public TradeHealthValuePoint(float healthRatio, float valueFactor)
    {
        HealthRatio = healthRatio <= 0f ? 0f : healthRatio >= 1f ? 1f : healthRatio;
        ValueFactor = Math.Max(0f, valueFactor);
    }

    public float HealthRatio { get; }

    public float ValueFactor { get; }
}

public sealed class TradeFeeCalculationResult
{
    public TradeFeeCalculationResult(
        bool accepted,
        int requiredFeeSilver,
        int baseFeeSilver,
        int fixedFeeSilver,
        IReadOnlyList<string> missingMarketValueDefs)
    {
        Accepted = accepted;
        RequiredFeeSilver = requiredFeeSilver;
        BaseFeeSilver = baseFeeSilver;
        FixedFeeSilver = fixedFeeSilver;
        MissingMarketValueDefs = missingMarketValueDefs;
    }

    public bool Accepted { get; }

    public int RequiredFeeSilver { get; }

    public int BaseFeeSilver { get; }

    public int FixedFeeSilver { get; }

    public IReadOnlyList<string> MissingMarketValueDefs { get; }
}
