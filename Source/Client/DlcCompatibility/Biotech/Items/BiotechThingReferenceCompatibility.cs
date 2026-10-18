using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

internal sealed class BiotechThingReferenceCompatibility
{
    private readonly Func<bool> enabled;
    private readonly BiotechReproductiveSources sources;

    internal BiotechThingReferenceCompatibility(Func<bool> enabled, BiotechReproductiveSources sources)
    {
        this.enabled = enabled;
        this.sources = sources;
    }

    private static readonly System.Reflection.FieldInfo GeneSetField = typeof(GeneSetHolderBase).GetField(
        "geneSet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    internal void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!enabled() || BiotechItemDefinitions.IsGeneSetHolder(def) || BiotechItemDefinitions.HasPawnSources(def))
        {
            return;
        }

        BiotechThingMetadata.Clear(item);
    }

    internal bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (!enabled() || !BiotechItemDefinitions.IsGeneSetHolder(def))
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

    internal bool IsUniqueThingReferenceRequest(ThingDef? def)
    {
        return enabled() && BiotechItemDefinitions.IsGeneSetHolder(def);
    }

    internal void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        if (!enabled() || metadataThing is null || reference is null)
        {
            return;
        }

        if (metadataThing is GeneSetHolderBase { GeneSet: not null } geneHolder)
        {
            BiotechThingMetadata.SetGeneDefNames(reference, geneHolder.GeneSet.GenesListForReading
                .Select(gene => gene.defName));
        }

        if (metadataThing is Xenogerm xenogerm)
        {
            BiotechThingMetadata.SetXenotypeName(reference, string.IsNullOrWhiteSpace(xenogerm.xenotypeName) ? null : xenogerm.xenotypeName);
            BiotechThingMetadata.SetXenotypeIconDefName(reference, xenogerm.iconDef?.defName);
        }

        sources.Append(metadataThing, reference);
    }

    internal bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        BiotechThingMetadata.Normalize(requirement);
        return !enabled()
            || (BiotechThingMetadata.GeneRequirementMatches(requirement, CandidateGeneDefNames(metadataThing))
                && BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, sources.Capture(metadataThing)));
    }

    internal bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        BiotechThingMetadata.Normalize(requirement);
        BiotechThingMetadata.Normalize(candidate);
        return !enabled()
            || BiotechTradeConstraints.Matches(requirement.Metadata, candidate.Metadata);
    }

    internal bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        BiotechThingMetadata.Normalize(reference);
        if (!enabled())
        {
            return true;
        }

        IReadOnlyList<string> geneDefNames = BiotechThingMetadata.GeneDefNames(reference);
        if (geneDefNames.Count > 0 && thing is GeneSetHolderBase)
        {
            List<GeneDef> genes = new();
            foreach (string geneDefName in geneDefNames)
            {
                GeneDef? gene = BiotechGeneRuntime.ResolveDef(geneDefName);
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
            }
            else if (thing is Xenogerm xenogerm)
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

        return sources.TryApply(reference, thing, out missingDefName);
    }

    internal int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        BiotechThingMetadata.Normalize(requirement);
        return enabled() ? BiotechTradeConstraints.RequirementStrictness(requirement.Metadata) : 0;
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
