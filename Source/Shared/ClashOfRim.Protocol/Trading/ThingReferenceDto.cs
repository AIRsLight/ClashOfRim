using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ThingReferenceDto
{
    public ThingReferenceDto(
        string globalKey,
        string? defName,
        int stackCount,
        string? quality = null,
        int? hitPoints = null,
        string? minifiedInnerDefName = null,
        string? minifiedInnerStuffDefName = null,
        string? minifiedInnerQuality = null,
        int? minifiedInnerHitPoints = null,
        bool? wornByCorpse = null,
        bool? biocoded = null,
        string? biocodedPawnLabel = null,
        string? biocodedPawnGlobalId = null,
        string? displayLabel = null,
        float? marketValue = null,
        bool? uniqueWeapon = null,
        IReadOnlyList<string>? uniqueWeaponTraits = null,
        PawnExchangePackageDto? pawnPackage = null,
        string? pawnPackageId = null,
        string? stuffDefName = null,
        int? maxHitPoints = null,
        int? minifiedInnerMaxHitPoints = null,
        Dictionary<string, string?>? metadata = null,
        ThingStatePackageDto? thingPackage = null,
        string? thingPackageId = null)
    {
        GlobalKey = globalKey;
        DefName = defName;
        StackCount = stackCount;
        Quality = quality;
        HitPoints = hitPoints;
        MinifiedInnerDefName = minifiedInnerDefName;
        MinifiedInnerStuffDefName = minifiedInnerStuffDefName;
        MinifiedInnerQuality = minifiedInnerQuality;
        MinifiedInnerHitPoints = minifiedInnerHitPoints;
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
        StuffDefName = stuffDefName;
        MaxHitPoints = maxHitPoints;
        MinifiedInnerMaxHitPoints = minifiedInnerMaxHitPoints;
        Metadata = metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        ThingPackage = thingPackage;
        ThingPackageId = thingPackageId;
    }

    public string GlobalKey { get; }

    public string? DefName { get; }

    public int StackCount { get; }

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

    public PawnExchangePackageDto? PawnPackage { get; }

    public string? PawnPackageId { get; }

    public Dictionary<string, string?> Metadata { get; }

    public ThingStatePackageDto? ThingPackage { get; }

    public string? ThingPackageId { get; }
}

public sealed class ThingStatePackageDto
{
    public ThingStatePackageDto(
        int packageVersion,
        string globalKey,
        string? defName,
        string? label,
        int stackCount,
        ThingScribePayloadDto scribe,
        string? fingerprint = null)
    {
        PackageVersion = packageVersion;
        GlobalKey = globalKey;
        DefName = defName;
        Label = label;
        StackCount = stackCount;
        Scribe = scribe;
        Fingerprint = fingerprint;
    }

    public int PackageVersion { get; }

    public string GlobalKey { get; }

    public string? DefName { get; }

    public string? Label { get; }

    public int StackCount { get; }

    public ThingScribePayloadDto Scribe { get; }

    public string? Fingerprint { get; }
}

public sealed class ThingScribePayloadDto
{
    public ThingScribePayloadDto(string xmlGzipBase64, string? xmlSha256, int uncompressedBytes)
    {
        XmlGzipBase64 = xmlGzipBase64;
        XmlSha256 = xmlSha256;
        UncompressedBytes = uncompressedBytes;
    }

    public string XmlGzipBase64 { get; }

    public string? XmlSha256 { get; }

    public int UncompressedBytes { get; }
}
