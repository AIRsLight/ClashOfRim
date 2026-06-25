using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public static class SnapshotSaveSanitizer
{
    private static readonly HashSet<string> RemoteSessionMapParentDefNames = new(StringComparer.Ordinal)
    {
        "ClashOfRim_RemoteSessionMapParent",
        "ClashOfRim_RemoteScoutMapParent",
        "ClashOfRim_RemoteRaidObservationMapParent",
        "ClashOfRim_RemoteRaidBattleMapParent"
    };

    public static byte[] RemoveTransientObservationState(
        byte[] saveBytes,
        out SnapshotSaveSanitizerResult result,
        bool removeRaidBattleSessions = false)
    {
        result = SnapshotSaveSanitizerResult.Empty;
        if (saveBytes.Length == 0)
        {
            return saveBytes;
        }

        if (!removeRaidBattleSessions && !ClashOfRimCompatibilityApi.HasSnapshotSaveSanitizers)
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
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            Log.Warning("[ClashOfRim][SnapshotSanitizer] Cannot parse save for transient observation cleanup: " + ex.Message);
            return saveBytes;
        }

        HashSet<string> removedWorldObjectLoadIds = removeRaidBattleSessions
            ? RemoveRemoteSessionWorldObjects(document)
            : new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> removedMapLoadIds = new(StringComparer.Ordinal);
        int removedMaps = RemoveMapsByParent(document, removedWorldObjectLoadIds, removedMapLoadIds);
        int removedPocketMaps = RemovePocketMapsBySourceMap(document, removedMapLoadIds, out HashSet<string> removedPocketMapParentLoadIds);
        removedMaps += RemoveMapsByParent(document, removedPocketMapParentLoadIds, removedMapLoadIds);
        int clearedComponents = removeRaidBattleSessions
            ? ClearRaidBattleComponentState(document)
            : 0;
        int compatibilityChanges = ClashOfRimCompatibilityApi.SanitizeSnapshotSave(document);

        result = new SnapshotSaveSanitizerResult(removedWorldObjectLoadIds.Count, removedMaps, clearedComponents);
        if (!result.Changed && compatibilityChanges <= 0)
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

        ClashLog.Message("[ClashOfRim][SnapshotSanitizer] Removed transient observation state: worldObjects="
            + result.RemovedWorldObjects
            + ", maps="
            + result.RemovedMaps
            + ", pocketMaps="
            + removedPocketMaps
            + ", components="
            + result.ClearedComponents
            + ", compatibility="
            + compatibilityChanges);
        return target.ToArray();
    }

    public static byte[] EnsureLineageMarker(byte[] saveBytes, string? snapshotId, string? lineageToken)
    {
        if (saveBytes.Length == 0 || string.IsNullOrWhiteSpace(snapshotId))
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
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            Log.Warning("[ClashOfRim][SnapshotSanitizer] Cannot parse save for lineage marker refresh: " + ex.Message);
            return saveBytes;
        }

        XElement root = document.Root ?? new XElement("savegame");
        if (document.Root is null)
        {
            document.Add(root);
        }

        XElement game = GetOrAdd(root, "game");
        XElement components = GetOrAdd(game, "components");
        XElement? component = components
            .Elements("li")
            .FirstOrDefault(item => IsClashOfRimGameComponentClass(item.Attribute("Class")?.Value));
        if (component is null)
        {
            component = new XElement("li", new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"));
            components.Add(component);
        }

        SetElement(component, "clashOfRimLineageSnapshotId", snapshotId!);
        SetElement(component, "clashOfRimLineageToken", lineageToken ?? string.Empty);

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

    public static byte[] RemovePendingAchievementQueue(byte[] saveBytes)
    {
        if (saveBytes.Length == 0)
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
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            Log.Warning("[ClashOfRim][SnapshotSanitizer] Cannot parse save for pending achievement cleanup: " + ex.Message);
            return saveBytes;
        }

        XElement? component = document.Root?
            .Element("game")?
            .Element("components")?
            .Elements("li")
            .FirstOrDefault(item => IsClashOfRimGameComponentClass(item.Attribute("Class")?.Value));
        XElement? pending = component?.Element("clashOfRimPendingAchievementCandidates");
        if (pending is null)
        {
            return saveBytes;
        }

        pending.Remove();

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

    private static HashSet<string> RemoveRemoteSessionWorldObjects(XDocument document)
    {
        var removedLoadIds = new HashSet<string>(StringComparer.Ordinal);
        List<XElement> worldObjects = document
            .Descendants("li")
            .Where(IsRaidBattleSessionWorldObject)
            .ToList();

        foreach (XElement worldObject in worldObjects)
        {
            string? id = worldObject.Element("ID")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                removedLoadIds.Add("WorldObject_" + id);
            }

            worldObject.Remove();
        }

        return removedLoadIds;
    }

    private static bool IsRaidBattleSessionWorldObject(XElement element)
    {
        string? def = element.Element("def")?.Value?.Trim();
        string? className = element.Attribute("Class")?.Value;
        if ((def is null || !RemoteSessionMapParentDefNames.Contains(def))
            && className?.IndexOf("RemoteSessionMapParent", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        string? mode = element.Element("clashOfRimMode")?.Value?.Trim();
        return string.Equals(mode, "RaidBattle", StringComparison.OrdinalIgnoreCase);
    }

    private static int RemoveMapsByParent(
        XDocument document,
        HashSet<string> removedWorldObjectLoadIds,
        HashSet<string> removedMapLoadIds)
    {
        if (removedWorldObjectLoadIds.Count == 0)
        {
            return 0;
        }

        List<XElement> maps = document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li")
            .Where(map => removedWorldObjectLoadIds.Contains(map.Element("mapInfo")?.Element("parent")?.Value?.Trim() ?? string.Empty))
            .ToList() ?? new List<XElement>();

        foreach (XElement map in maps)
        {
            string? uniqueId = map.Element("uniqueID")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(uniqueId))
            {
                removedMapLoadIds.Add("Map_" + uniqueId);
            }

            map.Remove();
        }

        return maps.Count;
    }

    private static int RemovePocketMapsBySourceMap(
        XDocument document,
        HashSet<string> removedMapLoadIds,
        out HashSet<string> removedPocketMapParentLoadIds)
    {
        removedPocketMapParentLoadIds = new HashSet<string>(StringComparer.Ordinal);
        if (removedMapLoadIds.Count == 0)
        {
            return 0;
        }

        List<XElement> pocketMaps = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("pocketMaps")?
            .Elements("li")
            .Where(pocketMap => removedMapLoadIds.Contains(NormalizeLoadReference(pocketMap.Element("sourceMap")?.Value)))
            .ToList() ?? new List<XElement>();

        foreach (XElement pocketMap in pocketMaps)
        {
            string? id = pocketMap.Element("ID")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                removedPocketMapParentLoadIds.Add("WorldObject_" + id);
            }

            pocketMap.Remove();
        }

        return pocketMaps.Count;
    }

    private static string NormalizeLoadReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value!.Trim();
        return normalized.StartsWith("@", StringComparison.Ordinal)
            ? normalized.Substring(1)
            : normalized;
    }

    private static int ClearRaidBattleComponentState(XDocument document)
    {
        int cleared = 0;
        foreach (XElement component in document.Descendants("li")
                     .Where(element => element.Element("clashOfRimActiveRaidBattleSession") is not null
                         || element.Element("clashOfRimActiveRemoteMapSession") is not null)
                     .ToList())
        {
            bool changed = false;
            XElement? activeRaid = component.Element("clashOfRimActiveRaidBattleSession");
            if (activeRaid is not null)
            {
                activeRaid.Remove();
                changed = true;
            }

            XElement? remote = component.Element("clashOfRimActiveRemoteMapSession");
            if (remote?.Element("kind")?.Value?.Trim() == "RaidBattle")
            {
                remote.Remove();
                changed = true;
            }

            if (changed)
            {
                cleared++;
            }
        }

        return cleared;
    }

    private static bool IsClashOfRimGameComponentClass(string? className)
    {
        return !string.IsNullOrWhiteSpace(className)
            && className!.IndexOf("AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal) >= 0;
    }

    private static XElement GetOrAdd(XElement parent, string name)
    {
        XElement? element = parent.Element(name);
        if (element is not null)
        {
            return element;
        }

        element = new XElement(name);
        parent.Add(element);
        return element;
    }

    private static void SetElement(XElement parent, string name, string value)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
        }
        else
        {
            element.Value = value;
        }
    }
}

public readonly struct SnapshotSaveSanitizerResult
{
    public SnapshotSaveSanitizerResult(int removedWorldObjects, int removedMaps, int clearedComponents)
    {
        RemovedWorldObjects = removedWorldObjects;
        RemovedMaps = removedMaps;
        ClearedComponents = clearedComponents;
    }

    public static SnapshotSaveSanitizerResult Empty { get; } = new(0, 0, 0);

    public int RemovedWorldObjects { get; }

    public int RemovedMaps { get; }

    public int ClearedComponents { get; }

    public bool Changed => RemovedWorldObjects > 0 || RemovedMaps > 0 || ClearedComponents > 0;
}
