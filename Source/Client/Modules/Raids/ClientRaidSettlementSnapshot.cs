using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Modules.Maps;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidSettlementSaveFailure(string Key, string? Message = null);

// Validate the captured raid map before packaging; keep all save state intact.
internal static class ClientRaidSettlementSnapshot
{
    internal static byte[]? Prepare(byte[] save, string eventId, string targetMapId, out ClientRaidSettlementSaveFailure? failure)
    {
        failure = null;
        if (save.Length == 0) { failure = new("StatusSettlementEmptySave"); return null; }
        if (string.IsNullOrWhiteSpace(targetMapId) || targetMapId.Trim() == "Map_")
        { failure = new("StatusSettlementMissingSourceMap"); return null; }
        XDocument document;
        try { using var source = new MemoryStream(save); document = XDocument.Load(source, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) when (ex is IOException or XmlException)
        { failure = new("StatusSettlementXmlFailed", ex.Message); return null; }
        var parent = document.Descendants("li").FirstOrDefault(element =>
            string.Equals(element.Element("clashOfRimMode")?.Value?.Trim(), ClientRemoteMapModes.RaidBattle, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(eventId) || element.Element("clashOfRimRelatedEventId")?.Value?.Trim() == eventId));
        if (parent is null) { failure = new("StatusSettlementMissingParent"); return null; }
        string? id = parent.Element("ID")?.Value?.Trim();
        string parentId = string.IsNullOrWhiteSpace(id) ? "" : "WorldObject_" + id;
        var map = document.Root?.Element("game")?.Element("maps")?.Elements("li").FirstOrDefault(element =>
            element.Element("mapInfo")?.Element("parent")?.Value?.Trim() == parentId);
        if (map is null) { failure = new("StatusSettlementMissingMap"); return null; }
        string? mapId = map.Element("uniqueID")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(mapId) || mapId == "Map_")
        { failure = new("StatusSettlementMissingLocalMapId"); return null; }
        using var target = new MemoryStream();
        using (var writer = XmlWriter.Create(target, new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = Encoding.UTF8, CloseOutput = false }))
            document.Save(writer);
        return target.ToArray();
    }
}
