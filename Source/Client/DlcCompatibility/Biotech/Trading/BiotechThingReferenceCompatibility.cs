using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    private const string MetadataGeneDefNames = "rimworld.biotech.geneDefNames";
    private const string MetadataTargetGeneDefName = "rimworld.biotech.targetGeneDefName";
    private const string MetadataXenotypeName = "rimworld.biotech.xenotypeName";
    private const string MetadataXenotypeIconDefName = "rimworld.biotech.xenotypeIconDefName";
    private const string MetadataReproductiveSourcePrefix = "rimworld.biotech.reproductiveSource.";
    private const string MetadataReproductiveSourceCount = MetadataReproductiveSourcePrefix + "count";
    private const string MetadataReproductiveSourceRole = "role";
    private const string MetadataReproductiveSourceGlobalId = "globalId";
    private const string MetadataReproductiveSourceOwnerUserId = "ownerUserId";
    private const string MetadataReproductiveSourceName = "name";
    private const string MetadataReproductiveSourceRaceDef = "raceDef";
    private const string MetadataReproductiveSourcePawnKindDef = "pawnKindDef";
    private const string MetadataReproductiveSourceGender = "gender";
    private const string MetadataReproductiveSourceDead = "dead";
    private const string MetadataReproductiveSourceFactionDef = "factionDef";
    private const string MetadataReproductiveSourceXenotypeDef = "xenotypeDef";
    private const string MetadataReproductiveSourceEndogeneDefNames = "endogeneDefNames";
    private const string MetadataReproductiveSourceXenogeneDefNames = "xenogeneDefNames";
    private const int MaxReproductiveSources = 4;

    internal static IReadOnlyList<string> GeneDefNames(ModThingReferenceDto? reference)
    {
        NormalizeBiotechMetadata(reference);
        return ReadMetadataList(reference, MetadataGeneDefNames);
    }

    internal static void SetGeneDefNames(ModThingReferenceDto? reference, IEnumerable<string>? geneDefNames)
    {
        WriteMetadataList(reference, MetadataGeneDefNames, geneDefNames);
    }

    internal static string? TargetGeneDefName(ModThingReferenceDto? reference)
    {
        return TargetGeneDefNames(reference).FirstOrDefault();
    }

    internal static IReadOnlyList<string> TargetGeneDefNames(ModThingReferenceDto? reference)
    {
        NormalizeBiotechMetadata(reference);
        return ReadMetadataList(reference, MetadataTargetGeneDefName);
    }

    internal static void SetTargetGeneDefName(ModThingReferenceDto? reference, string? geneDefName)
    {
        SetTargetGeneDefNames(reference, string.IsNullOrWhiteSpace(geneDefName)
            ? Array.Empty<string>()
            : new[] { geneDefName! });
    }

    internal static void SetTargetGeneDefNames(ModThingReferenceDto? reference, IEnumerable<string>? geneDefNames)
    {
        WriteMetadataList(reference, MetadataTargetGeneDefName, geneDefNames);
    }

    internal static string? XenotypeName(ModThingReferenceDto? reference)
    {
        NormalizeBiotechMetadata(reference);
        return ReadMetadataText(reference, MetadataXenotypeName);
    }

    internal static void SetXenotypeName(ModThingReferenceDto? reference, string? xenotypeName)
    {
        WriteMetadataText(reference, MetadataXenotypeName, xenotypeName);
    }

    internal static string? XenotypeIconDefName(ModThingReferenceDto? reference)
    {
        NormalizeBiotechMetadata(reference);
        return ReadMetadataText(reference, MetadataXenotypeIconDefName);
    }

    internal static void SetXenotypeIconDefName(ModThingReferenceDto? reference, string? iconDefName)
    {
        WriteMetadataText(reference, MetadataXenotypeIconDefName, iconDefName);
    }

    internal static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!HasBiotechTradeMetadata || IsGeneSetHolderDef(def) || IsReproductiveSourceCarrierDef(def))
        {
            return;
        }

        item.Metadata.Remove(MetadataGeneDefNames);
        item.Metadata.Remove(MetadataTargetGeneDefName);
        item.Metadata.Remove(MetadataXenotypeName);
        item.Metadata.Remove(MetadataXenotypeIconDefName);
        ClearReproductiveSourceMetadata(item);
    }

    internal static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!HasBiotechTradeMetadata || !IsGeneSetHolderDef(def))
        {
            return true;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            return TargetGeneDefNames(item).Count > 0;
        }

        return !string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal)
            || GeneDefNames(item).Count > 0;
    }

    internal static bool IsUniqueThingReferenceRequest(ThingDef? def)
    {
        return HasBiotechTradeMetadata && IsGeneSetHolderDef(def);
    }

    internal static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        if (!HasBiotechTradeMetadata || metadataThing is null || reference is null)
        {
            return;
        }

        if (metadataThing is GeneSetHolderBase { GeneSet: not null } geneHolder)
        {
            SetGeneDefNames(reference, geneHolder.GeneSet.GenesListForReading
                .Select(gene => gene.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        if (metadataThing is Xenogerm xenogerm)
        {
            SetXenotypeName(reference, string.IsNullOrWhiteSpace(xenogerm.xenotypeName) ? null : xenogerm.xenotypeName);
            SetXenotypeIconDefName(reference, xenogerm.iconDef?.defName);
        }

        AppendReproductiveSourceMetadata(metadataThing, reference);
    }

    internal static void NormalizeBiotechThingReferenceMetadata(ModThingReferenceDto reference)
    {
        NormalizeBiotechMetadata(reference);
    }

    internal static bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        NormalizeBiotechMetadata(requirement);
        return !HasBiotechTradeMetadata
            || (GeneRequirementMatches(requirement, CandidateGeneDefNames(metadataThing))
                && ReproductiveSourceRequirementMatches(requirement, CandidateReproductiveSources(metadataThing)));
    }

    internal static bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        NormalizeBiotechMetadata(requirement);
        NormalizeBiotechMetadata(candidate);
        return !HasBiotechTradeMetadata
            || (GeneRequirementMatches(requirement, GeneDefNames(candidate))
                && ReproductiveSourceRequirementMatches(requirement, ReproductiveSources(candidate)));
    }

    internal static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        NormalizeBiotechMetadata(reference);
        if (!HasBiotechTradeMetadata)
        {
            return true;
        }

        if (GeneDefNames(reference).Count > 0 && thing is GeneSetHolderBase)
        {
            List<GeneDef> genes = new();
            foreach (string geneDefName in GeneDefNames(reference))
            {
                GeneDef? gene = ResolveGeneDef(geneDefName);
                if (gene is null)
                {
                    missingDefName = geneDefName;
                    return false;
                }

                genes.Add(gene);
            }

            if (thing is Genepack genepack)
            {
                genepack.Initialize(genes);
                return TryApplyReproductiveSourceMetadata(reference, thing, out missingDefName);
            }

            if (thing is Xenogerm xenogerm)
            {
                Genepack sourcePack = (Genepack)ThingMaker.MakeThing(ThingDefOf.Genepack);
                sourcePack.Initialize(genes);
                string? xenotypeIconDefName = XenotypeIconDefName(reference);
                XenotypeIconDef icon = string.IsNullOrWhiteSpace(xenotypeIconDefName)
                    ? XenotypeIconDefOf.Basic
                    : DefDatabase<XenotypeIconDef>.GetNamedSilentFail(xenotypeIconDefName) ?? XenotypeIconDefOf.Basic;
                string? xenotypeName = XenotypeName(reference);
                xenogerm.Initialize(
                    new List<Genepack> { sourcePack },
                    string.IsNullOrWhiteSpace(xenotypeName)
                        ? ClashOfRimText.Key("ClashOfRim.Trade.GeneXenotypeName")
                        : xenotypeName!,
                    icon);
            }

        }

        return TryApplyReproductiveSourceMetadata(reference, thing, out missingDefName);
    }

    internal static int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        NormalizeBiotechMetadata(requirement);
        if (!HasBiotechTradeMetadata)
        {
            return 0;
        }

        int strictness = TargetGeneDefNames(requirement).Count > 0 ? 1_000_000 : 0;
        if (ReproductiveSources(requirement).Count > 0)
        {
            strictness += 900_000;
        }

        return strictness;
    }

    private static GeneDef? ResolveGeneDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<GeneDef>.GetNamedSilentFail(defName);
    }

    private static IReadOnlyCollection<string> CandidateGeneDefNames(Thing metadataThing)
    {
        return metadataThing is GeneSetHolderBase { GeneSet: not null } geneHolder
            ? geneHolder.GeneSet.GenesListForReading
                .Select(gene => gene.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .ToList()
            : Array.Empty<string>();
    }

    private static bool GeneRequirementMatches(ModThingReferenceDto requirement, IReadOnlyCollection<string>? candidateGeneDefNames)
    {
        NormalizeBiotechMetadata(requirement);
        IReadOnlyList<string> targetGeneDefNames = TargetGeneDefNames(requirement);
        if (targetGeneDefNames.Count == 0)
        {
            return true;
        }

        if (candidateGeneDefNames is null || candidateGeneDefNames.Count == 0)
        {
            return false;
        }

        return targetGeneDefNames.All(targetGeneDefName =>
            candidateGeneDefNames.Any(geneDefName => string.Equals(
                    geneDefName,
                    targetGeneDefName,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static void NormalizeBiotechMetadata(ModThingReferenceDto? reference)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
    }

    private static IReadOnlyList<string> ReadMetadataList(ModThingReferenceDto? reference, string key)
    {
        NormalizeBiotechMetadata(reference);
        if (reference is null
            || !reference.Metadata.TryGetValue(key, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(gene => gene.Trim())
            .Where(gene => !string.IsNullOrWhiteSpace(gene))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteMetadataList(ModThingReferenceDto? reference, string key, IEnumerable<string>? values)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        string[] normalized = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = string.Join(",", normalized);
    }

    private static string? ReadMetadataText(ModThingReferenceDto? reference, string key)
    {
        NormalizeBiotechMetadata(reference);
        return reference is not null
            && reference.Metadata.TryGetValue(key, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    private static void WriteMetadataText(ModThingReferenceDto? reference, string key, string? value)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(value))
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = value;
    }
}
