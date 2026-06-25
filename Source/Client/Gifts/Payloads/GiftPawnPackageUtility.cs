using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal static class GiftPawnPackageUtility
{
    public static ModPawnExchangePackageDto BuildPackage(
        Pawn pawn,
        string globalKey,
        string userId,
        string colonyId,
        string snapshotId,
        string containerKey)
    {
        var package = new ModPawnExchangePackageDto
        {
            PackageVersion = 1,
            Reference = BuildReference(pawn, globalKey, snapshotId, userId, colonyId),
            Identity = BuildIdentity(pawn),
            Appearance = new ModPawnExchangeAppearanceDto
            {
                DisplayName = pawn.LabelShort,
                BodyTypeDef = pawn.story?.bodyType?.defName,
                HeadTypeDef = pawn.story?.headType?.defName,
                HairDef = pawn.story?.hairDef?.defName,
                BeardDef = pawn.style?.beardDef?.defName,
                SkinColor = null,
                HairColor = null
            },
            Status = new ModPawnExchangeStatusDto
            {
                Dead = pawn.Dead,
                BiologicalAgeTicks = pawn.ageTracker?.AgeBiologicalTicks,
                ChronologicalAgeTicks = pawn.ageTracker?.AgeChronologicalTicks,
                DeathCauseDef = null,
                HealthState = pawn.health?.State.ToString()
            },
            Apparel = pawn.apparel?.WornApparel
                .Select(apparel => ToEquipmentItem(apparel, globalKey, container: "apparel"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Equipment = pawn.equipment?.AllEquipmentListForReading
                .Select(equipment => ToEquipmentItem(equipment, globalKey, container: "equipment"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Relationships = BuildOneLayerRelationshipStubs(pawn, userId, colonyId, snapshotId),
            Scribe = BuildScribePayload(
                pawn,
                referencedPawn => BuildRelatedPawnGlobalKey(referencedPawn, userId, colonyId, snapshotId),
                containerKey)
        };
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, package);
        return package;
    }

    public static bool TryRestore(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.AnimalGift,
            out pawn,
            out message);
    }

    public static bool TryRestoreTradePawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.TradePawn,
            out pawn,
            out message);
    }

    public static bool TryRestoreGiftPawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.GiftPawn,
            out pawn,
            out message);
    }

    public static bool TryRestoreCorpsePawn(ModPawnExchangePackageDto package, out Pawn? pawn, out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            package,
            PawnExchangeRestoreKind.CorpseGift,
            out pawn,
            out message,
            forcePlayerFaction: false);
    }

    private static ModCrossMapPawnReferenceDto BuildReference(
        Pawn pawn,
        string globalKey,
        string snapshotId,
        string? userId,
        string? colonyId)
    {
        return new ModCrossMapPawnReferenceDto
        {
            GlobalId = globalKey,
            SourceSnapshotId = snapshotId,
            Name = pawn.LabelShort,
            Dead = pawn.Dead,
            Faction = pawn.Faction?.def?.defName,
            Metadata = BuildPawnReferenceMetadata(pawn, userId, colonyId)
        };
    }

    private static Dictionary<string, string?> BuildPawnReferenceMetadata(Pawn pawn, string? userId, string? colonyId)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        ClashOfRimCompatibilityApi.CollectPawnReferenceMetadata(pawn, metadata, userId, colonyId);
        return metadata;
    }

    private static ModPawnExchangeIdentityDto BuildIdentity(Pawn pawn)
    {
        var identity = new ModPawnExchangeIdentityDto
        {
            ThingDef = pawn.def?.defName,
            PawnKindDef = pawn.kindDef?.defName,
            FactionDef = pawn.Faction?.def?.defName,
            Gender = pawn.gender.ToString()
        };
        return identity;
    }

    private static ModPawnExchangeEquipmentItemDto ToEquipmentItem(Thing thing, string pawnGlobalKey, string container)
    {
        QualityCategory quality;
        string? qualityValue = QualityUtility.TryGetQuality(thing, out quality)
            ? quality.ToString()
            : null;
        Apparel? apparel = thing as Apparel;
        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        bool traitWeapon = TradeThingReferenceUtility.IsWeaponWithTraits(thing);

        var item = new ModPawnExchangeEquipmentItemDto
        {
            GlobalId = $"{pawnGlobalKey}/{container}:{thing.ThingID}",
            Def = thing.def?.defName,
            Label = thing.LabelCapNoCount,
            StackCount = Math.Max(1, thing.stackCount),
            Quality = qualityValue,
            HitPoints = thing.def?.useHitPoints == true ? thing.HitPoints : null,
            WornByCorpse = apparel?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnGlobalId = biocoded ? pawnGlobalKey : null,
            UniqueWeapon = traitWeapon ? true : null,
            UniqueWeaponName = null,
            UniqueWeaponTraits = TradeThingReferenceUtility.WeaponTraitDefNames(thing)
        };
        return item;
    }

    private static List<ModPawnExchangeRelationshipStubDto> BuildOneLayerRelationshipStubs(
        Pawn pawn,
        string userId,
        string colonyId,
        string snapshotId)
    {
        if (pawn.relations?.DirectRelations is null)
        {
            return new List<ModPawnExchangeRelationshipStubDto>();
        }

        return pawn.relations.DirectRelations
            .Where(relation => relation?.otherPawn is not null && !string.IsNullOrWhiteSpace(relation.otherPawn.ThingID))
            .Take(128)
            .Select(relation => new ModPawnExchangeRelationshipStubDto
            {
                OtherPawnGlobalId = BuildRelatedPawnGlobalKey(relation.otherPawn, userId, colonyId, snapshotId),
                OtherPawnName = relation.otherPawn.LabelShort,
                OtherPawnDead = relation.otherPawn.Dead,
                RelationDef = relation.def?.defName
            })
            .ToList();
    }

    private static ModPawnScribePayloadDto? BuildScribePayload(Pawn pawn, Func<Pawn, string> globalIdResolver, string containerKey)
    {
        try
        {
            string? xml = TryCreatePawnScribeXml(pawn);
            if (string.IsNullOrWhiteSpace(xml))
            {
                Log.Warning("[ClashOfRim] Gift animal Scribe debug output was empty; package will use structured fields only.");
                return null;
            }

            return new ModPawnScribePayloadDto
            {
                Xml = xml!,
                XmlSha256 = ComputeSha256Hex(xml!),
                PawnReferenceReplacements = BuildScribePawnReferenceReplacements(pawn, globalIdResolver, containerKey)
            };
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            Log.Warning("[ClashOfRim] Failed to build gift animal Scribe payload: " + ex);
            return null;
        }
    }

    private static string? TryCreatePawnScribeXml(Pawn pawn)
    {
        FieldInfo? saverField = typeof(Scribe).GetField("saver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? saver = saverField?.GetValue(null);
        if (saver is null)
        {
            return null;
        }

        MethodInfo? method = saver.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "DebugOutputFor", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(Pawn));
            });
        return method?.Invoke(saver, new object[] { pawn }) as string;
    }

    private static List<ModPawnScribePawnReferenceReplacementDto> BuildScribePawnReferenceReplacements(
        Pawn pawn,
        Func<Pawn, string> globalIdResolver,
        string containerKey)
    {
        var replacements = new Dictionary<string, ModPawnScribePawnReferenceReplacementDto>(StringComparer.Ordinal);
        void AddReferencedPawn(Pawn? referencedPawn)
        {
            if (referencedPawn is null || referencedPawn == pawn)
            {
                return;
            }

            string sourceLoadId = referencedPawn.GetUniqueLoadID();
            if (string.IsNullOrWhiteSpace(sourceLoadId) || replacements.ContainsKey(sourceLoadId))
            {
                return;
            }

            string globalId = globalIdResolver(referencedPawn);
            replacements.Add(sourceLoadId, new ModPawnScribePawnReferenceReplacementDto
            {
                SourceLoadId = sourceLoadId,
                PlaceholderLoadId = "ClashOfRimGiftPlaceholderPawn_" + ShortHash(containerKey + globalId),
                Reference = BuildReference(
                    referencedPawn,
                    globalId,
                    sourceSnapshotIdFromGlobal(globalId),
                    userIdFromGlobal(globalId),
                    colonyIdFromGlobal(globalId))
            });
        }

        if (pawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations)
            {
                AddReferencedPawn(relation?.otherPawn);
            }
        }

        return replacements.Values.Take(128).ToList();
    }

    private static string? userIdFromGlobal(string globalId)
    {
        return TryParseOwnerColonyFromGlobalKey(globalId, out string? userId, out _)
            ? userId
            : null;
    }

    private static string? colonyIdFromGlobal(string globalId)
    {
        return TryParseOwnerColonyFromGlobalKey(globalId, out _, out string? colonyId)
            ? colonyId
            : null;
    }

    private static bool TryParseOwnerColonyFromGlobalKey(string globalId, out string? userId, out string? colonyId)
    {
        userId = ExtractGlobalSegment(globalId, "owner:", "/colony:");
        colonyId = ExtractGlobalSegment(globalId, "/colony:", "/snapshot:");
        return !string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(colonyId);
    }

    private static string? ExtractGlobalSegment(string value, string startMarker, string endMarker)
    {
        int start = value.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += startMarker.Length;
        int end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0 || end <= start)
        {
            return null;
        }

        return value.Substring(start, end - start);
    }

    private static string sourceSnapshotIdFromGlobal(string globalId)
    {
        const string marker = "/snapshot:";
        int start = globalId.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        int end = globalId.IndexOf('/', start);
        return end < 0
            ? globalId.Substring(start)
            : globalId.Substring(start, end - start);
    }

    private static string BuildRelatedPawnGlobalKey(Pawn pawn, string userId, string colonyId, string snapshotId)
    {
        return PawnGlobalIdUtility.Build(userId, pawn);
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return ToHexLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string ShortHash(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return ToHexLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(text))).Substring(0, 16);
    }

    private static string ToHexLower(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

}
