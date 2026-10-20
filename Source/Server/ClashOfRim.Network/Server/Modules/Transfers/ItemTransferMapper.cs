using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network.Modules.Transfers;

internal static class ItemTransferMapper
{
    private const string PawnMetadataThingDef = "clashofrim.pawn.thingDef";
    private const string PawnMetadataPawnKindDef = "clashofrim.pawn.pawnKindDef";
    private const string PawnMetadataGender = "clashofrim.pawn.gender";
    private const string PawnMetadataBiologicalAgeTicks = "clashofrim.pawn.biologicalAgeTicks";

    internal static EventThingReference ToEventThingReference(ThingReferenceDto thing, string? sourceSnapshotId)
    {
        return new EventThingReference(
            thing.GlobalKey,
            thing.DefName,
            thing.StackCount,
            sourceSnapshotId,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            ToPawnExchangePackage(thing.PawnPackage),
            thing.PawnPackageId,
            thing.StuffDefName,
            thing.MaxHitPoints,
            thing.MinifiedInnerMaxHitPoints,
            Metadata: CopyMetadata(thing.Metadata),
            ThingPackage: ToThingStatePackage(thing.ThingPackage),
            ThingPackageId: thing.ThingPackageId);
    }

    internal static ThingReferenceDto CloneThingReferenceWithStoredPackages(
        ThingReferenceDto thing,
        string? pawnPackageId,
        string? thingPackageId,
        Dictionary<string, string?> metadata)
    {
        return new ThingReferenceDto(
            thing.GlobalKey,
            thing.DefName,
            thing.StackCount,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            pawnPackage: null,
            pawnPackageId: pawnPackageId,
            stuffDefName: thing.StuffDefName,
            maxHitPoints: thing.MaxHitPoints,
            minifiedInnerMaxHitPoints: thing.MinifiedInnerMaxHitPoints,
            metadata: metadata,
            thingPackage: null,
            thingPackageId: thingPackageId);
    }

    internal static bool TryValidateInlinePawnPackages(
        IReadOnlyList<ThingReferenceDto> things,
        out ProtocolResponse? failure)
    {
        failure = null;
        foreach (ThingReferenceDto thing in things)
        {
            if (thing.PawnPackage is null)
            {
                continue;
            }

            if (!TryToPawnExchangePackage(thing.PawnPackage, out _, out string packageFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    ServerLocalization.Text("PawnPackage.Invalid", new Dictionary<string, string?> { ["MESSAGE"] = packageFailure }));
                return false;
            }
        }

        return true;
    }

    internal static bool TryValidateInlineThingPackages(
        IReadOnlyList<ThingReferenceDto> things,
        out ProtocolResponse? failure)
    {
        failure = null;
        foreach (ThingReferenceDto thing in things)
        {
            if (!ThingTransferPolicy.IsAcceptedConcreteReference(thing, out string policyFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    ServerLocalization.Text("ThingTransfer.Invalid", new Dictionary<string, string?> { ["MESSAGE"] = policyFailure }));
                return false;
            }

            if (thing.ThingPackage is null)
            {
                continue;
            }

            if (!TryToThingStatePackage(thing.ThingPackage, out _, out string packageFailure))
            {
                failure = ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    "invalid thing package: " + packageFailure);
                return false;
            }
        }

        return true;
    }

    internal static bool TryToPawnExchangePackage(
        PawnExchangePackageDto? dto,
        out PawnExchangePackage? package,
        out string failure)
    {
        package = null;
        failure = string.Empty;
        if (dto is null)
        {
            failure = ServerLocalization.Text("PawnPackage.ParseFailed");
            return false;
        }

        if (dto.Reference is null || string.IsNullOrWhiteSpace(dto.Reference.GlobalId))
        {
            failure = "missing reference";
            return false;
        }

        if (dto.Identity is null)
        {
            failure = "missing identity";
            return false;
        }

        if (dto.Appearance is null)
        {
            failure = "missing appearance";
            return false;
        }

        if (dto.Status is null)
        {
            failure = "missing status";
            return false;
        }

        if (dto.Scribe is null || string.IsNullOrWhiteSpace(dto.Scribe.Xml))
        {
            failure = ServerLocalization.Text("PawnPackage.MissingScribe");
            return false;
        }

        try
        {
            package = ToPawnExchangePackage(dto);
            if (package is null)
            {
                failure = ServerLocalization.Text("PawnPackage.ParseFailed");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            failure = ex.Message;
            return false;
        }
    }

    internal static ThingReferenceDto ToThingReferenceDto(EventThingReference thing)
    {
        return new ThingReferenceDto(
            thing.GlobalKey,
            thing.Def,
            thing.StackCount,
            thing.Quality,
            thing.HitPoints,
            thing.MinifiedInnerDefName,
            thing.MinifiedInnerStuffDefName,
            thing.MinifiedInnerQuality,
            thing.MinifiedInnerHitPoints,
            thing.WornByCorpse,
            thing.Biocoded,
            thing.BiocodedPawnLabel,
            thing.BiocodedPawnGlobalId,
            thing.DisplayLabel,
            thing.MarketValue,
            thing.UniqueWeapon,
            thing.UniqueWeaponTraits,
            ToPawnExchangePackageDto(thing.PawnPackage),
            thing.PawnPackageId,
            thing.StuffDefName,
            thing.MaxHitPoints,
            thing.MinifiedInnerMaxHitPoints,
            metadata: CopyMetadata(thing.Metadata),
            thingPackage: ToThingStatePackageDto(thing.ThingPackage),
            thingPackageId: thing.ThingPackageId);
    }

    internal static ThingStatePackageDto? ToThingStatePackageDto(ThingStatePackage? package)
    {
        return package is null
            ? null
            : new ThingStatePackageDto(
                package.PackageVersion,
                package.GlobalKey,
                package.DefName,
                package.Label,
                package.StackCount,
                new ThingScribePayloadDto(
                    package.Scribe.XmlGzipBase64,
                    package.Scribe.XmlSha256,
                    package.Scribe.UncompressedBytes),
                package.Fingerprint);
    }

    internal static PawnExchangePackageDto? ToPawnExchangePackageDto(PawnExchangePackage? package)
    {
        return package is null
            ? null
            : new PawnExchangePackageDto(
                package.PackageVersion,
                ToCrossMapPawnReferenceDto(package.Reference),
                new PawnExchangeIdentityDto(
                    package.Identity.ThingDef,
                    package.Identity.PawnKindDef,
                    package.Identity.FactionDef,
                    package.Identity.Gender),
                new PawnExchangeAppearanceDto(
                    package.Appearance.DisplayName,
                    package.Appearance.BodyTypeDef,
                    package.Appearance.HeadTypeDef,
                    package.Appearance.HairDef,
                    package.Appearance.BeardDef,
                    package.Appearance.SkinColor,
                    package.Appearance.HairColor),
                new PawnExchangeStatusDto(
                    package.Status.Dead,
                    package.Status.BiologicalAgeTicks,
                    package.Status.ChronologicalAgeTicks,
                    package.Status.DeathCauseDef,
                    package.Status.HealthState),
                package.Apparel.Select(ToPawnExchangeEquipmentItemDto).ToList(),
                package.Equipment.Select(ToPawnExchangeEquipmentItemDto).ToList(),
                package.Relationships.Select(ToPawnExchangeRelationshipStubDto).ToList(),
                ToPawnScribePayloadDto(package.Scribe),
                (package.Extensions ?? Array.Empty<PawnExchangeExtensionPackage>())
                    .Select(ToPawnExchangeExtensionPackageDto)
                    .ToList());
    }

    private static PawnExchangeExtensionPackageDto ToPawnExchangeExtensionPackageDto(PawnExchangeExtensionPackage extension)
    {
        return new PawnExchangeExtensionPackageDto(
            extension.ProviderId,
            extension.Kind,
            CopyMetadata(extension.Metadata),
            extension.PayloadJson);
    }

    private static CrossMapPawnReferenceDto ToCrossMapPawnReferenceDto(CrossMapPawnReference reference)
    {
        return new CrossMapPawnReferenceDto(
            reference.GlobalId,
            reference.SourceSnapshotId,
            reference.Name,
            reference.Dead,
            reference.Faction,
            CopyMetadata(reference.Metadata));
    }

    internal static Dictionary<string, string?> CopyMetadata(IReadOnlyDictionary<string, string?>? metadata)
    {
        return metadata is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static Dictionary<string, string?> EnrichPawnSummaryMetadata(
        IReadOnlyDictionary<string, string?>? source,
        PawnExchangePackage package)
    {
        Dictionary<string, string?> metadata = CopyMetadata(source);
        AddMetadataIfMissing(metadata, PawnMetadataThingDef, package.Identity.ThingDef);
        AddMetadataIfMissing(metadata, PawnMetadataPawnKindDef, package.Identity.PawnKindDef);
        AddMetadataIfMissing(metadata, PawnMetadataGender, package.Identity.Gender);
        AddMetadataIfMissing(
            metadata,
            PawnMetadataBiologicalAgeTicks,
            package.Status.BiologicalAgeTicks?.ToString(CultureInfo.InvariantCulture));
        return metadata;
    }

    private static void AddMetadataIfMissing(Dictionary<string, string?> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !metadata.ContainsKey(key))
        {
            metadata[key] = value;
        }
    }

    private static PawnExchangeEquipmentItemDto ToPawnExchangeEquipmentItemDto(PawnExchangeEquipmentItem item)
    {
        return new PawnExchangeEquipmentItemDto(
            item.GlobalId,
            item.Def,
            item.Label,
            item.StackCount,
            item.Quality,
            item.HitPoints,
            item.WornByCorpse,
            item.Biocoded,
            item.BiocodedPawnGlobalId,
            item.UniqueWeapon,
            item.UniqueWeaponName,
            item.UniqueWeaponTraits);
    }

    private static PawnExchangeRelationshipStubDto ToPawnExchangeRelationshipStubDto(PawnExchangeRelationshipStub stub)
    {
        return new PawnExchangeRelationshipStubDto(
            stub.OtherPawnGlobalId,
            stub.OtherPawnName,
            stub.OtherPawnDead,
            stub.RelationDef,
            stub.OtherPawnThingDef,
            stub.OtherPawnKindDef);
    }

    private static PawnScribePayloadDto? ToPawnScribePayloadDto(PawnScribePayload? scribe)
    {
        return scribe is null
            ? null
            : new PawnScribePayloadDto(
                scribe.Xml,
                scribe.XmlSha256,
                scribe.PawnReferenceReplacements.Select(ToPawnScribeReplacementDto).ToList());
    }

    private static PawnScribePawnReferenceReplacementDto ToPawnScribeReplacementDto(PawnScribePawnReferenceReplacement replacement)
    {
        return new PawnScribePawnReferenceReplacementDto(
            replacement.SourceLoadId,
            replacement.PlaceholderLoadId,
            ToCrossMapPawnReferenceDto(replacement.Reference));
    }

    internal static CrossMapPawnReference? ToCrossMapPawnReference(CrossMapPawnReferenceDto? dto)
    {
        return dto is null
            ? null
            : new CrossMapPawnReference(
                dto.GlobalId,
                dto.SourceSnapshotId,
                dto.Name,
                dto.Dead,
                dto.Faction,
                dto.Metadata);
    }

    private static PawnExchangePackage? ToPawnExchangePackage(PawnExchangePackageDto? dto)
    {
        return dto is null
            ? null
            : new PawnExchangePackage(
                dto.PackageVersion,
                ToCrossMapPawnReference(dto.Reference)!,
                new PawnExchangeIdentity(
                    dto.Identity.ThingDef,
                    dto.Identity.PawnKindDef,
                    dto.Identity.FactionDef,
                    dto.Identity.Gender),
                new PawnExchangeAppearance(
                    dto.Appearance.DisplayName,
                    dto.Appearance.BodyTypeDef,
                    dto.Appearance.HeadTypeDef,
                    dto.Appearance.HairDef,
                    dto.Appearance.BeardDef,
                    dto.Appearance.SkinColor,
                    dto.Appearance.HairColor),
                new PawnExchangeStatus(
                    dto.Status.Dead,
                    dto.Status.BiologicalAgeTicks,
                    dto.Status.ChronologicalAgeTicks,
                    dto.Status.DeathCauseDef,
                    dto.Status.HealthState),
                (dto.Apparel ?? Array.Empty<PawnExchangeEquipmentItemDto>())
                    .Where(item => item is not null)
                    .Select(ToPawnExchangeEquipmentItem)
                    .ToList(),
                (dto.Equipment ?? Array.Empty<PawnExchangeEquipmentItemDto>())
                    .Where(item => item is not null)
                    .Select(ToPawnExchangeEquipmentItem)
                    .ToList(),
                (dto.Relationships ?? Array.Empty<PawnExchangeRelationshipStubDto>())
                    .Where(relationship => relationship is not null)
                    .Select(ToPawnExchangeRelationshipStub)
                    .ToList(),
                ToPawnScribePayload(dto.Scribe),
                (dto.Extensions ?? Array.Empty<PawnExchangeExtensionPackageDto>())
                    .Where(extension => extension is not null)
                    .Select(ToPawnExchangeExtensionPackage)
                    .ToList());
    }

    internal static bool TryToThingStatePackage(
        ThingStatePackageDto? dto,
        out ThingStatePackage? package,
        out string failure)
    {
        package = null;
        failure = string.Empty;
        if (dto is null)
        {
            failure = "missing thing package";
            return false;
        }

        if (dto.Scribe is null)
        {
            failure = "missing thing scribe";
            return false;
        }

        try
        {
            package = ToThingStatePackage(dto);
            if (package is null)
            {
                failure = "thing package parse failed";
                return false;
            }

            SafeThingStatePackageSerializer.Serialize(package);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static ThingStatePackage? ToThingStatePackage(ThingStatePackageDto? dto)
    {
        return dto?.Scribe is null
            ? null
            : new ThingStatePackage(
                dto.PackageVersion,
                dto.GlobalKey,
                dto.DefName,
                dto.Label,
                dto.StackCount,
                new ThingScribePayload(
                    dto.Scribe.XmlGzipBase64,
                    dto.Scribe.XmlSha256,
                    dto.Scribe.UncompressedBytes),
                dto.Fingerprint);
    }

    private static PawnExchangeExtensionPackage ToPawnExchangeExtensionPackage(PawnExchangeExtensionPackageDto dto)
    {
        return new PawnExchangeExtensionPackage(
            dto.ProviderId,
            dto.Kind,
            CopyMetadata(dto.Metadata),
            dto.PayloadJson);
    }

    private static PawnExchangeEquipmentItem ToPawnExchangeEquipmentItem(PawnExchangeEquipmentItemDto dto)
    {
        return new PawnExchangeEquipmentItem(
            dto.GlobalId,
            dto.Def,
            dto.Label,
            dto.StackCount,
            dto.Quality,
            dto.HitPoints,
            dto.WornByCorpse,
            dto.Biocoded,
            dto.BiocodedPawnGlobalId,
            dto.UniqueWeapon,
            dto.UniqueWeaponName,
            dto.UniqueWeaponTraits);
    }

    private static PawnExchangeRelationshipStub ToPawnExchangeRelationshipStub(PawnExchangeRelationshipStubDto dto)
    {
        return new PawnExchangeRelationshipStub(
            dto.OtherPawnGlobalId,
            dto.OtherPawnName,
            dto.OtherPawnDead,
            dto.RelationDef,
            dto.OtherPawnThingDef,
            dto.OtherPawnKindDef);
    }

    private static PawnScribePayload? ToPawnScribePayload(PawnScribePayloadDto? dto)
    {
        return dto is null
            ? null
            : new PawnScribePayload(
                dto.Xml,
                dto.XmlSha256,
                (dto.PawnReferenceReplacements ?? Array.Empty<PawnScribePawnReferenceReplacementDto>())
                    .Where(replacement => replacement is not null)
                    .Select(ToPawnScribeReplacement)
                    .ToList());
    }

    private static PawnScribePawnReferenceReplacement ToPawnScribeReplacement(PawnScribePawnReferenceReplacementDto dto)
    {
        return new PawnScribePawnReferenceReplacement(
            dto.SourceLoadId,
            dto.PlaceholderLoadId,
            ToCrossMapPawnReference(dto.Reference)!);
    }

}
