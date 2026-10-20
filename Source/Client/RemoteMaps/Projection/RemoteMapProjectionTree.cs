using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Snapshots;

namespace AIRsLight.ClashOfRim.RemoteMaps;

// Reserve and rewrite IDs together, before splitting the graph into maps loaded on entry.
internal sealed class RemoteMapProjectionTree
{
    internal XElement Maps { get; }
    internal XElement PocketParents { get; }
    internal IReadOnlyDictionary<string, string> SourceMapIds { get; }
    private readonly Dictionary<string, string> references;

    internal RemoteMapProjectionTree(XDocument source, XElement rootMap, string carrierId, Func<int> nextMapId, Func<int> nextParentId)
    {
        var parentIds = new HashSet<string>(StringComparer.Ordinal) { Reference(rootMap.Element("mapInfo")?.Element("parent")) };
        var parents = SavePocketMapTree.IncludeDescendantParents(source, parentIds);
        var descendants = source.Root!.Element("game")!.Element("maps")!.Elements("li")
            .Where(m => !ReferenceEquals(m, rootMap) && parentIds.Contains(Reference(m.Element("mapInfo")?.Element("parent"))));
        Maps = new XElement("maps", new[] { rootMap }.Concat(descendants).Select(m => new XElement(m)));
        PocketParents = new XElement("pocketMaps", parents.Select(p => new XElement(p)));
        references = new Dictionary<string, string>(StringComparer.Ordinal);
        references.Add(Reference(rootMap.Element("mapInfo")?.Element("parent")), carrierId);
        var sourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in Maps.Elements())
        {
            string original = map.Element("uniqueID")!.Value.Trim();
            string local = nextMapId().ToString(System.Globalization.CultureInfo.InvariantCulture);
            references.Add("Map_" + original, "Map_" + local);
            sourceIds.Add(local, original);
            map.SetElementValue("uniqueID", local);
        }
        foreach (var parent in PocketParents.Elements())
        {
            string original = "WorldObject_" + parent.Element("ID")!.Value.Trim();
            string local = nextParentId().ToString(System.Globalization.CultureInfo.InvariantCulture);
            references.Add(original, "WorldObject_" + local);
            parent.SetElementValue("ID", local);
        }
        RewriteReferences(Maps);
        RewriteReferences(PocketParents);
        Maps.Elements().First().Element("mapInfo")!.SetElementValue("parent", carrierId);
        SourceMapIds = sourceIds;
    }

    internal void RewriteReferences(XElement root)
    {
        // Simultaneous replacement: a new local ID may equal a different source ID.
        foreach (var leaf in root.Descendants().Where(e => !e.HasElements))
        {
            string value = leaf.Value.Trim();
            if (references.TryGetValue(value.TrimStart('@'), out var replacement))
                leaf.Value = value.StartsWith("@", StringComparison.Ordinal) ? "@" + replacement : replacement;
        }
    }

    private static string Reference(XElement? value) => value?.Value.Trim().TrimStart('@') ?? "";
}
