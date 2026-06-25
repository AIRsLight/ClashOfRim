using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteMapThingIdentitySnapshotInjector
{
    public static byte[] Inject(
        byte[] saveBytes,
        IEnumerable<RemoteMapThingIdentityRecord>? identities,
        out int injectedCount)
    {
        injectedCount = 0;
        if (saveBytes is null || saveBytes.Length == 0)
        {
            return saveBytes ?? Array.Empty<byte>();
        }

        Dictionary<string, Dictionary<string, RemoteMapThingIdentityRecord>> byMap = BuildIdentityIndex(identities);
        if (byMap.Count == 0)
        {
            return saveBytes;
        }

        XDocument document;
        try
        {
            using var source = new MemoryStream(saveBytes);
            using XmlReader reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            Log.Warning("[ClashOfRim][RemoteMapIdentity] Cannot parse save for thing identity injection: " + ex.Message);
            return saveBytes;
        }

        foreach (XElement map in document.Root?
                     .Element("game")?
                     .Element("maps")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string mapUniqueId = NormalizeMapId(map.Element("uniqueID")?.Value);
            if (string.IsNullOrWhiteSpace(mapUniqueId)
                || !byMap.TryGetValue(mapUniqueId, out Dictionary<string, RemoteMapThingIdentityRecord>? mapIdentities))
            {
                continue;
            }

            foreach (XElement thing in map.Descendants()
                         .Where(element => element.Element("def") is not null && element.Element("id") is not null))
            {
                string projectedThingId = NormalizeThingId(thing.Element("id")?.Value);
                if (string.IsNullOrWhiteSpace(projectedThingId)
                    || !mapIdentities.TryGetValue(projectedThingId, out RemoteMapThingIdentityRecord? identity))
                {
                    continue;
                }

                if (SetElementIfDifferent(thing, "clashOfRimOriginalThingId", identity.OriginalThingId))
                {
                    injectedCount++;
                }

                SetElementIfDifferent(thing, "clashOfRimProjectedThingId", projectedThingId);
            }
        }

        if (injectedCount == 0)
        {
            return saveBytes;
        }

        using var target = new MemoryStream();
        using (var writer = XmlWriter.Create(target, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = Encoding.UTF8,
            CloseOutput = false
        }))
        {
            document.Save(writer);
        }

        return target.ToArray();
    }

    private static Dictionary<string, Dictionary<string, RemoteMapThingIdentityRecord>> BuildIdentityIndex(
        IEnumerable<RemoteMapThingIdentityRecord>? identities)
    {
        var byMap = new Dictionary<string, Dictionary<string, RemoteMapThingIdentityRecord>>(StringComparer.Ordinal);
        if (identities is null)
        {
            return byMap;
        }

        foreach (RemoteMapThingIdentityRecord identity in identities)
        {
            string mapUniqueId = NormalizeMapId(identity.MapUniqueId);
            string projectedThingId = NormalizeThingId(identity.ProjectedThingId);
            string originalThingId = NormalizeThingId(identity.OriginalThingId);
            if (string.IsNullOrWhiteSpace(mapUniqueId)
                || string.IsNullOrWhiteSpace(projectedThingId)
                || string.IsNullOrWhiteSpace(originalThingId))
            {
                continue;
            }

            if (!byMap.TryGetValue(mapUniqueId, out Dictionary<string, RemoteMapThingIdentityRecord>? mapIdentities))
            {
                mapIdentities = new Dictionary<string, RemoteMapThingIdentityRecord>(StringComparer.Ordinal);
                byMap[mapUniqueId] = mapIdentities;
            }

            mapIdentities[projectedThingId] = identity;
        }

        return byMap;
    }

    private static bool SetElementIfDifferent(XElement parent, string name, string value)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
            return true;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }

    private static string NormalizeMapId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }

    private static string NormalizeThingId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Thing_", StringComparison.Ordinal)
            ? trimmed.Substring("Thing_".Length)
            : trimmed;
    }
}
