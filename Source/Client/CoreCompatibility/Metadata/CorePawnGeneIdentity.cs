using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

// Cosmetic genes exist in Core 1.7 too; their local identities are not DLC state.
internal static class CorePawnGeneIdentity
{
    public static BuiltInCoreCompatibilityPackageDescriptor Descriptor { get; } =
        new("clashofrim.core.pawn-gene-identity", Apply, order: 120);

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterRemoteMapIntegerLoadIdRewriter(
            "Gene_", IsGeneNode, () => Find.UniqueIDsManager.GetNextGeneID());
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(LocalizePawn);
    }

    private static bool IsGeneNode(XElement element)
    {
        if (element.Element("def") is null) return false;
        string className = element.Attribute("Class")?.Value ?? string.Empty;
        return className.IndexOf("Gene", StringComparison.Ordinal) >= 0
            || (element.Name.LocalName == "li" && element.Ancestors().Any(a =>
                string.Equals(a.Name.LocalName, "endogenes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name.LocalName, "xenogenes", StringComparison.OrdinalIgnoreCase)));
    }

    private static void LocalizePawn(Pawn pawn)
    {
        LocalizeGenes(pawn.genes?.Xenogenes);
        LocalizeGenes(pawn.genes?.Endogenes);
    }

    internal static int LocalizeGenes(IEnumerable<Gene>? genes)
    {
        if (genes is null || Find.UniqueIDsManager is null) return 0;
        int count = 0;
        foreach (Gene gene in genes)
        {
            if (gene?.def is null) continue;
            gene.loadID = Find.UniqueIDsManager.GetNextGeneID();
            count++;
        }
        return count;
    }
}
