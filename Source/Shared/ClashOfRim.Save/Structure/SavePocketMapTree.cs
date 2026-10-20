using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Snapshots;

// Shared as source with the game client: no engine or server dependency.
internal static class SavePocketMapTree
{
    internal static List<XElement> IncludeDescendantParents(XDocument document, HashSet<string> parentLoadIds)
    {
        var game = document.Root?.Element("game");
        var maps = game?.Element("maps")?.Elements("li").ToList() ?? new List<XElement>();
        var pockets = game?.Element("world")?.Element("pocketMaps")?.Elements("li").ToList() ?? new List<XElement>();
        var descendants = new List<XElement>();
        while (true)
        {
            var sourceIds = new HashSet<string>(maps.Where(m => parentLoadIds.Contains(Reference(m.Element("mapInfo")?.Element("parent"))))
                .Select(m => "Map_" + m.Element("uniqueID")?.Value.Trim()), StringComparer.Ordinal);
            var children = pockets.Where(p => sourceIds.Contains(Reference(p.Element("sourceMap")))).ToList();
            if (children.Count == 0) return descendants;
            foreach (var child in children)
            {
                parentLoadIds.Add("WorldObject_" + child.Element("ID")?.Value.Trim());
                descendants.Add(child);
                pockets.Remove(child);
            }
        }
    }

    private static string Reference(XElement? element) => element?.Value.Trim().TrimStart('@') ?? "";
}
