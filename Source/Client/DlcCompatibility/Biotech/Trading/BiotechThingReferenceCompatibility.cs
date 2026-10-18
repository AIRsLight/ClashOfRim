using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
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
    private static readonly System.Reflection.FieldInfo GeneSetField = typeof(GeneSetHolderBase).GetField(
        "geneSet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    internal static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!HasBiotechTradeMetadata || IsGeneSetHolderDef(def) || IsReproductiveSourceCarrierDef(def))
        {
            return;
        }

        BiotechThingMetadata.Clear(item);
    }

    internal static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!HasBiotechTradeMetadata || !IsGeneSetHolderDef(def))
        {
            return true;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            return BiotechThingMetadata.TargetGeneDefNames(item).Count > 0;
        }

        return !string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal)
            || BiotechThingMetadata.GeneDefNames(item).Count > 0;
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
            BiotechThingMetadata.SetGeneDefNames(reference, geneHolder.GeneSet.GenesListForReading
                .Select(gene => gene.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        if (metadataThing is Xenogerm xenogerm)
        {
            BiotechThingMetadata.SetXenotypeName(reference, string.IsNullOrWhiteSpace(xenogerm.xenotypeName) ? null : xenogerm.xenotypeName);
            BiotechThingMetadata.SetXenotypeIconDefName(reference, xenogerm.iconDef?.defName);
        }

        AppendReproductiveSourceMetadata(metadataThing, reference);
    }

    internal static bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        BiotechThingMetadata.Normalize(requirement);
        return !HasBiotechTradeMetadata
            || (BiotechThingMetadata.GeneRequirementMatches(requirement, CandidateGeneDefNames(metadataThing))
                && BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, CandidateReproductiveSources(metadataThing)));
    }

    internal static bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        BiotechThingMetadata.Normalize(requirement);
        BiotechThingMetadata.Normalize(candidate);
        return !HasBiotechTradeMetadata
            || BiotechTradeConstraints.Matches(requirement.Metadata, candidate.Metadata);
    }

    internal static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        BiotechThingMetadata.Normalize(reference);
        if (!HasBiotechTradeMetadata)
        {
            return true;
        }

        if (BiotechThingMetadata.GeneDefNames(reference).Count > 0 && thing is GeneSetHolderBase)
        {
            List<GeneDef> genes = new();
            foreach (string geneDefName in BiotechThingMetadata.GeneDefNames(reference))
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
                string? xenotypeIconDefName = BiotechThingMetadata.XenotypeIconDefName(reference);
                XenotypeIconDef icon = string.IsNullOrWhiteSpace(xenotypeIconDefName)
                    ? XenotypeIconDefOf.Basic
                    : DefDatabase<XenotypeIconDef>.GetNamedSilentFail(xenotypeIconDefName) ?? XenotypeIconDefOf.Basic;
                string? xenotypeName = BiotechThingMetadata.XenotypeName(reference);
                xenogerm.Initialize(
                    new List<Genepack> { sourcePack },
                    string.IsNullOrWhiteSpace(xenotypeName)
                        ? ClashOfRimText.Key("ClashOfRim.Trade.GeneXenotypeName")
                        : xenotypeName!,
                    icon);
            }
            else
            {
                // Embryos and other base holders have no public Initialize API. Preserve
                // the transmitted genes rather than regenerating them from parent stubs.
                var geneSet = new GeneSet();
                foreach (GeneDef gene in genes) geneSet.AddGene(gene);
                GeneSetField.SetValue(thing, geneSet);
            }
        }

        return TryApplyReproductiveSourceMetadata(reference, thing, out missingDefName);
    }

    internal static int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        BiotechThingMetadata.Normalize(requirement);
        return HasBiotechTradeMetadata ? BiotechTradeConstraints.RequirementStrictness(requirement.Metadata) : 0;
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

}
