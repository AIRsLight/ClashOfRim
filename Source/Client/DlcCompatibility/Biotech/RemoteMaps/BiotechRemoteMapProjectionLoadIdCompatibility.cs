using System;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static bool IsBiotechGeneLoadIdNode(XElement element)
    {
        if (!HasBiotechPawnExchange || element is null)
        {
            return false;
        }

        if (element.Element("def") is null)
        {
            return false;
        }

        string nodeName = element.Name.LocalName;
        string className = element.Attribute("Class")?.Value ?? string.Empty;
        bool isGeneClass = className.IndexOf("Gene", StringComparison.Ordinal) >= 0;
        bool isGeneListItem = string.Equals(nodeName, "li", StringComparison.OrdinalIgnoreCase)
            && (HasAncestorNamed(element, "endogenes") || HasAncestorNamed(element, "xenogenes"));
        return isGeneClass || isGeneListItem;
    }

    internal static int NextBiotechGeneLoadId()
    {
        return Find.UniqueIDsManager.GetNextGeneID();
    }

    internal static string? BuildBiotechAreaLoadId(XElement area, int id)
    {
        if (!HasBiotechWorldPollution)
        {
            return null;
        }

        string className = area.Attribute("Class")?.Value ?? string.Empty;
        return className.Contains("Area_PollutionClear")
            ? "Area_" + id + "_PollutionClear"
            : null;
    }

    private static bool HasAncestorNamed(XElement element, string name)
    {
        return element.Ancestors().Any(ancestor => string.Equals(ancestor.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    }
}
