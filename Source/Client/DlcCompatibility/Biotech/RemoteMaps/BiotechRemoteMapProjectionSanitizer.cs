using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static void SanitizeBiotechRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        if (!HasBiotechPawnExchange)
        {
            return;
        }

        SanitizePawnGeneReferences(mapElement);
        SanitizePawnGeneReferences(referencePawnsElement);
    }

    private static void SanitizePawnGeneReferences(XElement mapElement)
    {
        int pawnLinksRewritten = 0;
        int overriddenLinksCleared = 0;
        int hediffGeneLinksCleared = 0;
        int chemicalDependencyHediffsRemoved = 0;
        foreach (XElement pawn in mapElement
                     .Descendants("thing")
                     .Concat(mapElement.Descendants("li"))
                     .Where(IsProjectionPawnElement)
                     .ToList())
        {
            XElement? genes = pawn.Element("genes");
            if (genes is null || IsNullElement(genes))
            {
                continue;
            }

            chemicalDependencyHediffsRemoved += RemoveProjectionChemicalDependencyHediffs(pawn);
            string? pawnId = pawn.Element("id")?.Value?.Trim();
            string? pawnLoadId = string.IsNullOrWhiteSpace(pawnId) ? null : "Thing_" + pawnId;
            List<XElement> geneElements = genes
                .Descendants("li")
                .Where(gene => gene.Element("def") is not null && gene.Element("loadID") is not null)
                .ToList();
            var validGeneLoadIds = new HashSet<string>(
                geneElements
                    .Select(gene => gene.Element("loadID")?.Value?.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => "Gene_" + id),
                StringComparer.Ordinal);

            foreach (XElement gene in geneElements)
            {
                XElement? genePawn = gene.Element("pawn");
                if (!string.IsNullOrWhiteSpace(pawnLoadId)
                    && genePawn is not null
                    && !string.Equals(genePawn.Value.Trim(), pawnLoadId, StringComparison.Ordinal))
                {
                    genePawn.Value = pawnLoadId!;
                    pawnLinksRewritten++;
                }

                XElement? overriddenByGene = gene.Element("overriddenByGene");
                if (overriddenByGene is null)
                {
                    continue;
                }

                string value = overriddenByGene.Value.Trim();
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!validGeneLoadIds.Contains(value))
                {
                    overriddenByGene.Value = "null";
                    overriddenLinksCleared++;
                }
            }

            foreach (XElement geneReference in pawn
                         .Descendants()
                         .Where(IsSavedGeneReferenceElement)
                         .ToList())
            {
                string value = geneReference.Value.Trim();
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!validGeneLoadIds.Contains(value))
                {
                    geneReference.Value = "null";
                    hediffGeneLinksCleared++;
                }
            }
        }

        if (pawnLinksRewritten > 0 || overriddenLinksCleared > 0 || hediffGeneLinksCleared > 0 || chemicalDependencyHediffsRemoved > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection][Biotech] Sanitized pawn gene references: pawnLinksRewritten="
                + pawnLinksRewritten
                + ", overriddenLinksCleared="
                + overriddenLinksCleared
                + ", hediffGeneLinksCleared="
                + hediffGeneLinksCleared
                + ", chemicalDependencyHediffsRemoved="
                + chemicalDependencyHediffsRemoved
                + ".");
        }
    }

    private static int RemoveProjectionChemicalDependencyHediffs(XElement pawn)
    {
        int removed = 0;
        foreach (XElement hediff in pawn
                     .Descendants("hediffs")
                     .Elements("li")
                     .Where(IsChemicalDependencyHediff)
                     .ToList())
        {
            hediff.Remove();
            removed++;
        }

        return removed;
    }

    private static bool IsChemicalDependencyHediff(XElement hediff)
    {
        string className = hediff.Attribute("Class")?.Value ?? string.Empty;
        string defName = hediff.Element("def")?.Value?.Trim() ?? string.Empty;
        return className.IndexOf("Hediff_ChemicalDependency", StringComparison.Ordinal) >= 0
            || string.Equals(defName, "GeneticDrugNeed", StringComparison.Ordinal);
    }

    private static bool IsProjectionPawnElement(XElement element)
    {
        return element.Element("kindDef") is not null
            && element.Element("pather") is not null
            && element.Element("jobs") is not null;
    }

    private static bool IsNullElement(XElement element)
    {
        return string.Equals(element.Attribute("IsNull")?.Value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSavedGeneReferenceElement(XElement element)
    {
        string name = element.Name.LocalName;
        if (!string.Equals(name, "sourceGene", StringComparison.Ordinal)
            && !string.Equals(name, "linkedGene", StringComparison.Ordinal)
            && !string.Equals(name, "gene", StringComparison.Ordinal))
        {
            return false;
        }

        string value = element.Value.Trim();
        return value.StartsWith("Gene_", StringComparison.Ordinal);
    }

}
