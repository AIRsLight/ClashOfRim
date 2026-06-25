using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Save;

public static class RaidSettlementDiffer
{
    // These are scenario-critical or map-provided objects. Raid settlement must
    // never remove or damage them, even when the battle map no longer contains
    // the object after combat cleanup or player interaction.
    private static readonly HashSet<string> HardProtectedSettlementThingDefs = new(StringComparer.Ordinal)
    {
        "ClashOfRim_CaravanDeliveryPoint",
        "ClashOfRim_DefensePoint",
        "GeothermalVent",
        "Gravcore",
        "SteamGeyser",
        "VoidMonolith"
    };

    public static RaidSettlementDiffResult CompareByDisappearance(
        SaveSnapshotIndex originalSnapshot,
        SaveSnapshotIndex returnedSnapshot,
        RaidSettlementPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(originalSnapshot);
        ArgumentNullException.ThrowIfNull(returnedSnapshot);

        return CompareByDisappearance(originalSnapshot.Things, returnedSnapshot.Things, policy);
    }

    public static RaidSettlementDiffResult CompareByDisappearance(
        IEnumerable<ThingSummary> originalThings,
        IEnumerable<ThingSummary> returnedThings,
        RaidSettlementPolicy? policy = null)
    {
        return CompareByDisappearance(originalThings, returnedThings, policy, thing => thing.GlobalKey);
    }

    public static RaidSettlementDiffResult CompareByDisappearance(
        IEnumerable<ThingSummary> originalThings,
        IEnumerable<ThingSummary> returnedThings,
        RaidSettlementPolicy? policy,
        Func<ThingSummary, string?> settlementKeySelector)
    {
        return CompareByDisappearance(
            originalThings,
            returnedThings,
            policy,
            settlementKeySelector,
            settlementKeySelector);
    }

    public static RaidSettlementDiffResult CompareByDisappearance(
        IEnumerable<ThingSummary> originalThings,
        IEnumerable<ThingSummary> returnedThings,
        RaidSettlementPolicy? policy,
        Func<ThingSummary, string?> originalSettlementKeySelector,
        Func<ThingSummary, string?> returnedSettlementKeySelector)
    {
        ArgumentNullException.ThrowIfNull(originalThings);
        ArgumentNullException.ThrowIfNull(returnedThings);
        ArgumentNullException.ThrowIfNull(originalSettlementKeySelector);
        ArgumentNullException.ThrowIfNull(returnedSettlementKeySelector);

        Dictionary<string, ThingSummary> returnedByKey = BuildIncludedThingByKey(
            returnedThings,
            policy,
            returnedSettlementKeySelector);
        Dictionary<string, ThingSummary> originalByKey = BuildIncludedThingByKey(
            originalThings,
            policy,
            originalSettlementKeySelector);

        RaidSettlementPolicy activePolicy = policy ?? RaidSettlementPolicy.FullLoss;
        HashSet<string> missingDamageOnlyContainerLocalIds = MissingDamageOnlyContainerLocalIds(
            originalByKey,
            returnedByKey);
        var missing = new List<ThingSummary>();
        var losses = new List<RaidSettlementLoss>();
        foreach (KeyValuePair<string, ThingSummary> pair in originalByKey)
        {
            if (!returnedByKey.TryGetValue(pair.Key, out ThingSummary? returnedThing))
            {
                missing.Add(pair.Value);
                losses.Add(CalculateLoss(
                    pair.Value,
                    activePolicy,
                    forceFullLoss: ShouldForceFullLossBecauseContainerMissing(
                        pair.Value,
                        missingDamageOnlyContainerLocalIds)));
                continue;
            }

            int originalStackCount = ParseStackCount(pair.Value.StackCount);
            int returnedStackCount = ParseStackCount(returnedThing.StackCount);
            int stolenStackCount = originalStackCount - returnedStackCount;
            if (stolenStackCount > 0)
            {
                losses.Add(CalculateLoss(pair.Value, activePolicy, stolenStackCount, returnedStackCount));
                continue;
            }

            RaidSettlementLoss? damagedBuildingLoss = CalculateExistingBuildingDamageLoss(pair.Value, returnedThing, activePolicy);
            if (damagedBuildingLoss is not null)
            {
                losses.Add(damagedBuildingLoss);
            }
        }

        int ignoredExtraThingCount = 0;
        foreach (string key in returnedByKey.Keys)
        {
            if (!originalByKey.ContainsKey(key))
            {
                ignoredExtraThingCount++;
            }
        }

        return new RaidSettlementDiffResult(
            missing,
            losses,
            ignoredExtraThingCount,
            activePolicy.LossRatio);
    }

    public static RaidSettlementDiffResult CompareByDisappearance(
        IEnumerable<ThingSummary> originalThings,
        IEnumerable<ThingSummary> returnedThings,
        RaidSettlementPolicy? policy,
        Func<ThingSummary, IEnumerable<string>> originalSettlementKeySelector,
        Func<ThingSummary, IEnumerable<string>> returnedSettlementKeySelector)
    {
        ArgumentNullException.ThrowIfNull(originalThings);
        ArgumentNullException.ThrowIfNull(returnedThings);
        ArgumentNullException.ThrowIfNull(originalSettlementKeySelector);
        ArgumentNullException.ThrowIfNull(returnedSettlementKeySelector);

        RaidSettlementPolicy activePolicy = policy ?? RaidSettlementPolicy.FullLoss;
        Dictionary<string, ThingSummary> returnedByKey = BuildIncludedThingByKey(
            returnedThings,
            policy,
            returnedSettlementKeySelector);
        List<ThingSummary> includedOriginalThings = originalThings
            .Where(thing => ShouldIncludeInSettlement(thing, policy))
            .ToList();
        HashSet<string> missingDamageOnlyContainerLocalIds = MissingDamageOnlyContainerLocalIds(
            includedOriginalThings,
            returnedByKey,
            originalSettlementKeySelector);

        var originalKeys = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<ThingSummary>();
        var losses = new List<RaidSettlementLoss>();
        foreach (ThingSummary originalThing in includedOriginalThings)
        {
            IReadOnlyList<string> keys = NormalizeKeys(originalSettlementKeySelector(originalThing));
            if (keys.Count == 0)
            {
                continue;
            }

            foreach (string key in keys)
            {
                originalKeys.Add(key);
            }

            ThingSummary? returnedThing = null;
            foreach (string key in keys)
            {
                if (returnedByKey.TryGetValue(key, out returnedThing))
                {
                    break;
                }
            }

            if (returnedThing is null)
            {
                missing.Add(originalThing);
                losses.Add(CalculateLoss(
                    originalThing,
                    activePolicy,
                    forceFullLoss: ShouldForceFullLossBecauseContainerMissing(
                        originalThing,
                        missingDamageOnlyContainerLocalIds)));
                continue;
            }

            int originalStackCount = ParseStackCount(originalThing.StackCount);
            int returnedStackCount = ParseStackCount(returnedThing.StackCount);
            int stolenStackCount = originalStackCount - returnedStackCount;
            if (stolenStackCount > 0)
            {
                losses.Add(CalculateLoss(originalThing, activePolicy, stolenStackCount, returnedStackCount));
                continue;
            }

            RaidSettlementLoss? damagedBuildingLoss = CalculateExistingBuildingDamageLoss(originalThing, returnedThing, activePolicy);
            if (damagedBuildingLoss is not null)
            {
                losses.Add(damagedBuildingLoss);
            }
        }

        int ignoredExtraThingCount = returnedByKey
            .Where(pair => !originalKeys.Contains(pair.Key))
            .Select(pair => pair.Value.GlobalKey)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new RaidSettlementDiffResult(
            missing,
            losses,
            ignoredExtraThingCount,
            activePolicy.LossRatio);
    }

    private static Dictionary<string, ThingSummary> BuildIncludedThingByKey(
        IEnumerable<ThingSummary> things,
        RaidSettlementPolicy? policy,
        Func<ThingSummary, string?> settlementKeySelector)
    {
        var byKey = new Dictionary<string, ThingSummary>(StringComparer.Ordinal);
        foreach (ThingSummary thing in things)
        {
            if (!ShouldIncludeInSettlement(thing, policy))
            {
                continue;
            }

            string? key = settlementKeySelector(thing);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (byKey.TryGetValue(key!, out ThingSummary? existing))
            {
                byKey[key!] = MergeSettlementThingStack(existing, thing);
                continue;
            }

            byKey[key!] = thing;
        }

        return byKey;
    }

    private static Dictionary<string, ThingSummary> BuildIncludedThingByKey(
        IEnumerable<ThingSummary> things,
        RaidSettlementPolicy? policy,
        Func<ThingSummary, IEnumerable<string>> settlementKeySelector)
    {
        var byKey = new Dictionary<string, ThingSummary>(StringComparer.Ordinal);
        foreach (ThingSummary thing in things)
        {
            if (!ShouldIncludeInSettlement(thing, policy))
            {
                continue;
            }

            foreach (string key in NormalizeKeys(settlementKeySelector(thing)))
            {
                if (byKey.TryGetValue(key, out ThingSummary? existing))
                {
                    byKey[key] = MergeSettlementThingStack(existing, thing);
                    continue;
                }

                byKey[key] = thing;
            }
        }

        return byKey;
    }

    private static IReadOnlyList<string> NormalizeKeys(IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return Array.Empty<string>();
        }

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static ThingSummary MergeSettlementThingStack(ThingSummary first, ThingSummary second)
    {
        int stackCount = ParseStackCount(first.StackCount) + ParseStackCount(second.StackCount);
        return first with
        {
            StackCount = stackCount.ToString()
        };
    }

    private static HashSet<string> MissingDamageOnlyContainerLocalIds(
        IReadOnlyDictionary<string, ThingSummary> originalByKey,
        IReadOnlyDictionary<string, ThingSummary> returnedByKey)
    {
        return originalByKey
            .Where(pair => pair.Value.SettlementDamageOnly
                && !string.IsNullOrWhiteSpace(pair.Value.LocalId)
                && !returnedByKey.ContainsKey(pair.Key))
            .Select(pair => pair.Value.LocalId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> MissingDamageOnlyContainerLocalIds(
        IReadOnlyList<ThingSummary> originalThings,
        IReadOnlyDictionary<string, ThingSummary> returnedByKey,
        Func<ThingSummary, IEnumerable<string>> originalSettlementKeySelector)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThingSummary originalThing in originalThings)
        {
            if (!originalThing.SettlementDamageOnly || string.IsNullOrWhiteSpace(originalThing.LocalId))
            {
                continue;
            }

            IReadOnlyList<string> keys = NormalizeKeys(originalSettlementKeySelector(originalThing));
            if (keys.Count == 0)
            {
                continue;
            }

            if (!keys.Any(returnedByKey.ContainsKey))
            {
                missing.Add(originalThing.LocalId);
            }
        }

        return missing;
    }

    private static bool ShouldForceFullLossBecauseContainerMissing(
        ThingSummary thing,
        IReadOnlySet<string> missingDamageOnlyContainerLocalIds)
    {
        return !thing.IsPawn
            && !string.IsNullOrWhiteSpace(thing.SettlementAssetKind)
            && !string.IsNullOrWhiteSpace(thing.ContainerLocalId)
            && missingDamageOnlyContainerLocalIds.Contains(thing.ContainerLocalId);
    }

    public static RaidSettlementLoss CalculateLoss(ThingSummary thing, RaidSettlementPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(policy);

        int stackCount = ParseStackCount(thing.StackCount);
        return CalculateLoss(thing, policy, stackCount, returnedStackCount: null);
    }

    private static RaidSettlementLoss CalculateLoss(
        ThingSummary thing,
        RaidSettlementPolicy policy,
        bool forceFullLoss)
    {
        int stackCount = ParseStackCount(thing.StackCount);
        return CalculateLoss(thing, policy, stackCount, returnedStackCount: null, forceFullLoss);
    }

    public static RaidSettlementLoss CalculateLoss(
        ThingSummary thing,
        RaidSettlementPolicy policy,
        int stolenStackCount,
        int? returnedStackCount)
    {
        return CalculateLoss(thing, policy, stolenStackCount, returnedStackCount, forceFullLoss: false);
    }

    private static RaidSettlementLoss CalculateLoss(
        ThingSummary thing,
        RaidSettlementPolicy policy,
        int stolenStackCount,
        int? returnedStackCount,
        bool forceFullLoss)
    {
        ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(policy);

        int originalStackCount = ParseStackCount(thing.StackCount);
        int boundedStolenStackCount = Math.Clamp(stolenStackCount, 0, originalStackCount);
        if (forceFullLoss || policy.IsTrap(thing.Def))
        {
            return new RaidSettlementLoss(
                thing,
                originalStackCount,
                returnedStackCount,
                boundedStolenStackCount,
                BaseLossCapCount: originalStackCount,
                FractionalCapChance: 0,
                FractionalRoll: 0,
                MaxLossCount: originalStackCount,
                LossCount: boundedStolenStackCount,
                RemainingHitPointsAfterDamage: null);
        }

        double exactLossCap = originalStackCount * policy.LossRatio;
        int baseLossCapCount = (int)Math.Floor(exactLossCap);
        double fractionalCapChance = exactLossCap - baseLossCapCount;
        double fractionalRoll = StableUnitInterval($"{policy.EventId}|{thing.GlobalKey}");
        int maxLossCount = baseLossCapCount + (fractionalRoll < fractionalCapChance ? 1 : 0);
        int lossCount = Math.Min(boundedStolenStackCount, maxLossCount);
        int? remainingHitPointsAfterDamage = CalculateBuildingDamage(
            thing,
            policy,
            boundedStolenStackCount,
            returnedStackCount,
            lossCount);
        if (remainingHitPointsAfterDamage.HasValue && !policy.IsPackableBuilding(thing.Def))
        {
            lossCount = 0;
        }

        return new RaidSettlementLoss(
            thing,
            originalStackCount,
            returnedStackCount,
            boundedStolenStackCount,
            baseLossCapCount,
            fractionalCapChance,
            fractionalRoll,
            Math.Min(maxLossCount, originalStackCount),
            lossCount,
            remainingHitPointsAfterDamage);
    }

    private static int? CalculateBuildingDamage(
        ThingSummary thing,
        RaidSettlementPolicy policy,
        int boundedStolenStackCount,
        int? returnedStackCount,
        int lossCount)
    {
        if (returnedStackCount.HasValue
            || boundedStolenStackCount <= 0
            || policy.BuildingHitPointsLossRatio <= 0
            || !IsDamageableSettlementAsset(thing, policy))
        {
            return null;
        }

        if (lossCount > 0 && policy.IsPackableBuilding(thing.Def) && !thing.SettlementDamageOnly)
        {
            return null;
        }

        int hitPoints = ParseHitPoints(thing.HitPoints);
        if (hitPoints <= 1)
        {
            return null;
        }

        int estimatedMaxHitPoints = Math.Max(hitPoints, policy.EstimatedMaxHitPoints(thing.Def, thing.Stuff) ?? hitPoints);
        int minimumRemainingHitPoints = policy.MinimumRemainingHitPointsRatio <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(estimatedMaxHitPoints * policy.MinimumRemainingHitPointsRatio));
        int reduced = Math.Max(1, (int)Math.Ceiling(hitPoints * (1.0 - policy.BuildingHitPointsLossRatio)));
        int remainingHitPoints = Math.Max(minimumRemainingHitPoints, reduced);
        return remainingHitPoints >= hitPoints
            ? null
            : Math.Min(hitPoints - 1, remainingHitPoints);
    }

    private static RaidSettlementLoss? CalculateExistingBuildingDamageLoss(
        ThingSummary originalThing,
        ThingSummary returnedThing,
        RaidSettlementPolicy policy)
    {
        int? remainingHitPointsAfterDamage = CalculateExistingBuildingDamage(
            originalThing,
            returnedThing,
            policy);
        if (!remainingHitPointsAfterDamage.HasValue)
        {
            return null;
        }

        int originalStackCount = ParseStackCount(originalThing.StackCount);
        return new RaidSettlementLoss(
            originalThing,
            originalStackCount,
            originalStackCount,
            StolenStackCount: 0,
            BaseLossCapCount: 0,
            FractionalCapChance: 0,
            FractionalRoll: 0,
            MaxLossCount: 0,
            LossCount: 0,
            remainingHitPointsAfterDamage);
    }

    private static int? CalculateExistingBuildingDamage(
        ThingSummary originalThing,
        ThingSummary returnedThing,
        RaidSettlementPolicy policy)
    {
        if (policy.BuildingHitPointsLossRatio <= 0 || !IsDamageableSettlementAsset(originalThing, policy))
        {
            return null;
        }

        int originalHitPoints = ParseHitPoints(originalThing.HitPoints);
        int returnedHitPoints = ParseHitPoints(returnedThing.HitPoints);
        if (originalHitPoints <= 1
            || returnedHitPoints <= 0
            || returnedHitPoints >= originalHitPoints)
        {
            return null;
        }

        int estimatedMaxHitPoints = Math.Max(
            originalHitPoints,
            policy.EstimatedMaxHitPoints(originalThing.Def, originalThing.Stuff) ?? originalHitPoints);
        int minimumRemainingHitPoints = policy.MinimumRemainingHitPointsRatio <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(estimatedMaxHitPoints * policy.MinimumRemainingHitPointsRatio));
        int lossRatioProtectedHitPoints = Math.Max(
            1,
            (int)Math.Ceiling(originalHitPoints * (1.0 - policy.BuildingHitPointsLossRatio)));
        int remainingHitPoints = Math.Max(
            returnedHitPoints,
            Math.Max(minimumRemainingHitPoints, lossRatioProtectedHitPoints));
        return remainingHitPoints >= originalHitPoints
            ? null
            : Math.Min(originalHitPoints - 1, remainingHitPoints);
    }

    private static bool IsDamageableSettlementAsset(ThingSummary thing, RaidSettlementPolicy policy)
    {
        return thing.SettlementDamageOnly || policy.IsKnownBuilding(thing.Def);
    }

    private static int ParseStackCount(string? stackCount)
    {
        if (int.TryParse(stackCount, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        return 1;
    }

    private static int ParseHitPoints(string? hitPoints)
    {
        if (int.TryParse(hitPoints, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        return 0;
    }

    private static bool ShouldIncludeInSettlement(ThingSummary thing, RaidSettlementPolicy? policy)
    {
        if (IsBuildingSummary(thing)
            && policy is not null
            && !policy.IsKnownBuilding(thing.Def)
            && !policy.IsTrap(thing.Def)
            && !IsHiddenTrapProxy(thing))
        {
            return false;
        }

        return !thing.IsPawn
            && !RaidSettlementBattlefieldResiduePolicy.IsBattlefieldResidue(thing.Class, thing.Def)
            && (string.IsNullOrWhiteSpace(thing.Def)
                || (!HardProtectedSettlementThingDefs.Contains(thing.Def)
                    && policy?.IgnoredThingDefNames.Contains(thing.Def) != true));
    }

    private static bool IsBuildingSummary(ThingSummary thing)
    {
        return thing.Class?.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHiddenTrapProxy(ThingSummary thing)
    {
        return string.Equals(thing.Def, "ClashOfRim_HiddenTrapProxy", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(thing.ClashOfRimOriginalThingId);
    }

    private static double StableUnitInterval(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        ulong value = ((ulong)hash[0] << 56)
            | ((ulong)hash[1] << 48)
            | ((ulong)hash[2] << 40)
            | ((ulong)hash[3] << 32)
            | ((ulong)hash[4] << 24)
            | ((ulong)hash[5] << 16)
            | ((ulong)hash[6] << 8)
            | hash[7];

        return (value >> 11) * (1.0 / (1UL << 53));
    }
}
