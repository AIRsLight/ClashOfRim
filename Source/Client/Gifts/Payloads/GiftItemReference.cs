using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftItemReference
{
    public GiftItemReference(
        string globalKey,
        string? defName,
        int stackCount,
        string? sourceSnapshotId,
        string? quality = null,
        int? hitPoints = null,
        string? stuffDefName = null,
        int? maxHitPoints = null,
        string? minifiedInnerDefName = null,
        string? minifiedInnerStuffDefName = null,
        string? minifiedInnerQuality = null,
        int? minifiedInnerHitPoints = null,
        int? minifiedInnerMaxHitPoints = null,
        bool? wornByCorpse = null,
        bool? biocoded = null,
        string? biocodedPawnLabel = null,
        string? biocodedPawnGlobalId = null,
        string? displayLabel = null,
        float? marketValue = null,
        bool? uniqueWeapon = null,
        IReadOnlyList<string>? uniqueWeaponTraits = null,
        ModPawnExchangePackageDto? pawnPackage = null,
        string? pawnPackageId = null,
        IReadOnlyDictionary<string, string?>? metadata = null)
    {
        GlobalKey = globalKey;
        DefName = defName;
        StackCount = stackCount;
        SourceSnapshotId = sourceSnapshotId;
        Quality = quality;
        HitPoints = hitPoints;
        StuffDefName = stuffDefName;
        MaxHitPoints = maxHitPoints;
        MinifiedInnerDefName = minifiedInnerDefName;
        MinifiedInnerStuffDefName = minifiedInnerStuffDefName;
        MinifiedInnerQuality = minifiedInnerQuality;
        MinifiedInnerHitPoints = minifiedInnerHitPoints;
        MinifiedInnerMaxHitPoints = minifiedInnerMaxHitPoints;
        WornByCorpse = wornByCorpse;
        Biocoded = biocoded;
        BiocodedPawnLabel = biocodedPawnLabel;
        BiocodedPawnGlobalId = biocodedPawnGlobalId;
        DisplayLabel = displayLabel;
        MarketValue = marketValue;
        UniqueWeapon = uniqueWeapon;
        UniqueWeaponTraits = uniqueWeaponTraits ?? Array.Empty<string>();
        PawnPackage = pawnPackage;
        PawnPackageId = pawnPackageId;
        Metadata = metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    public string GlobalKey { get; }

    public string? DefName { get; }

    public int StackCount { get; }

    public string? SourceSnapshotId { get; }

    public string? Quality { get; }

    public int? HitPoints { get; }

    public string? StuffDefName { get; }

    public int? MaxHitPoints { get; }

    public string? MinifiedInnerDefName { get; }

    public string? MinifiedInnerStuffDefName { get; }

    public string? MinifiedInnerQuality { get; }

    public int? MinifiedInnerHitPoints { get; }

    public int? MinifiedInnerMaxHitPoints { get; }

    public bool? WornByCorpse { get; }

    public bool? Biocoded { get; }

    public string? BiocodedPawnLabel { get; }

    public string? BiocodedPawnGlobalId { get; }

    public string? DisplayLabel { get; }

    public float? MarketValue { get; }

    public bool? UniqueWeapon { get; }

    public IReadOnlyList<string> UniqueWeaponTraits { get; }

    public ModPawnExchangePackageDto? PawnPackage { get; set; }

    public string? PawnPackageId { get; }

    public IReadOnlyDictionary<string, string?> Metadata { get; }

    public bool SkipLanding { get; set; }

    public string? SkipReason { get; set; }
}
