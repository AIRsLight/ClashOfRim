using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

// Owns serialized, unvisited maps. Nothing here registers a Map or a PocketMapParent.
internal sealed class RemoteMapDeferredPockets : IExposable
{
    private string xml = "<pockets />";
    private string packageJson = "";
    private string lordsJson = "[]";
    private List<ILoadReferenceable> references = new();
    private XElement? pockets;
    private static readonly JsonSerializerOptions LordOptions = new() { IncludeFields = true };
    private XElement Pockets => pockets ??= XElement.Parse(xml);
    internal int Count => Pockets.Elements().Count();
    internal bool Retains(ILoadReferenceable reference) => references.Contains(reference);

    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving) xml = Pockets.ToString(SaveOptions.DisableFormatting);
        Scribe_Values.Look(ref xml, "maps", "<pockets />");
        Scribe_Values.Look(ref packageJson, "package", "");
        Scribe_Values.Look(ref lordsJson, "lords", "[]");
        Scribe_Collections.Look(ref references, "references", true, LookMode.Reference);
        if (Scribe.mode == LoadSaveMode.LoadingVars) pockets = null;
        references ??= new();
    }

    internal void Prepare(RemoteMapProjectionTree tree, ModSnapshotPackageMetadataDto package, RemoteSessionMapParent owner)
    {
        pockets = new XElement("pockets");
        packageJson = ClientProtocolJson.Serialize(package);
        lordsJson = JsonSerializer.Serialize(owner.RemoteNpcLordSnapshots, LordOptions);
        var maps = tree.Maps.Elements().ToList();
        // Detach every forward portal link before copying any map, including nested entrances.
        foreach (var map in maps.Skip(1))
        {
            string mapId = "Map_" + map.Element("uniqueID")!.Value;
            var entrance = tree.Maps.Descendants("pocketMap").Single(e => e.Value == mapId).Parent!;
            var parent = tree.PocketParents.Elements().Single(p => "WorldObject_" + p.Element("ID")!.Value == map.Element("mapInfo")!.Element("parent")!.Value);
            pockets.Add(new XElement("pocket", new XAttribute("entrance", entrance.Element("id")!.Value),
                new XAttribute("exit", entrance.Element("exit")!.Value),
                new XAttribute("source", tree.SourceMapIds[map.Element("uniqueID")!.Value]),
                new XAttribute("map", mapId), new XAttribute("sourceMap", parent.Element("sourceMap")!.Value)));
            entrance.SetElementValue("pocketMap", "null");
            entrance.SetElementValue("exit", "null");
        }
        foreach (var entry in pockets.Elements())
        {
            var map = maps.Single(m => "Map_" + m.Element("uniqueID")!.Value == (string)entry.Attribute("map")!);
            var parent = tree.PocketParents.Elements().Single(p => "WorldObject_" + p.Element("ID")!.Value == map.Element("mapInfo")!.Element("parent")!.Value);
            entry.Add(new XElement("savegame", new XElement("pocketMaps", new XElement(parent)), new XElement("maps", new XElement(map))));
        }
        tree.Maps.Elements().Skip(1).Remove();
        tree.PocketParents.RemoveNodes();
    }

    internal bool Contains(MapPortal portal) => Find(portal) is not null;
    private XElement? Find(MapPortal portal) => Pockets.Elements().FirstOrDefault(e => (string?)e.Attribute("entrance") == portal.ThingID
        && (string?)e.Attribute("sourceMap") == portal.Map?.GetUniqueLoadID());

    internal void RememberReferences(IEnumerable<ILoadReferenceable> loaded)
    {
        var wanted = Pockets.Descendants().Where(e => !e.HasElements).Select(e => e.Value.TrimStart('@')).ToHashSet(StringComparer.Ordinal);
        references = references.Concat(loaded).Where(r => r is not null && wanted.Contains(r.GetUniqueLoadID()))
            .GroupBy(r => r.GetUniqueLoadID(), StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    internal Map? Load(MapPortal portal, RemoteSessionMapParent owner)
    {
        var entry = Find(portal);
        if (entry is null) return null;
        Map? map = null;
        try
        {
            var document = new XDocument(entry.Element("savegame")!);
            IEnumerable<ILoadReferenceable> loadedReferences = Array.Empty<ILoadReferenceable>();
            if (!RemoteMapRuntimeLoader.TryLoad(document, owner, references, out map, out var failure,
                loaded => loadedReferences = loaded, isRoot: false))
                throw new InvalidOperationException(failure);
            owner.SetRemoteNpcLordSnapshots(JsonSerializer.Deserialize<List<RemoteNpcLordSnapshot>>(lordsJson, LordOptions));
            var package = ClientProtocolJson.Deserialize<ModSnapshotPackageMetadataDto>(packageJson, type => type);
            RemoteObservationMapPostProcessor.Apply(map!, owner, owner.Mode, package);
            var exit = map!.listerThings.AllThings.OfType<PocketMapExit>().Single(e => e.GetUniqueLoadID() == (string)entry.Attribute("exit")!);
            RegisterIdentities(owner, document.Root!.Element("maps")!.Elements().Single(), (string)entry.Attribute("source")!);
            AccessTools.Field(typeof(MapPortal), "pocketMap").SetValue(portal, map);
            portal.exit = exit;
            entry.Remove();
            RememberReferences(loadedReferences);
            return map;
        }
        catch (Exception ex)
        {
            // Keep the snapshot for retry; never replace a failed import with a fresh cave.
            if (map is not null && Current.Game.Maps.Contains(map)) PocketMapUtility.DestroyPocketMap(map);
            Log.Error("[ClashOfRim] Could not load saved remote pocket at " + portal.ThingID + ": " + ex);
            return null;
        }
    }

    internal void ForgetDescendants(string sourceMapId)
    {
        foreach (var entry in Pockets.Elements().Where(e => (string?)e.Attribute("sourceMap") == sourceMapId).ToList())
        {
            ForgetDescendants((string)entry.Attribute("map")!);
            entry.Remove();
        }
        RememberReferences(Array.Empty<ILoadReferenceable>());
    }

    internal void NotifyMapRemoved(string mapId)
    {
        // A failed import is still pending and owns its descendants for retry.
        if (!Pockets.Elements().Any(e => (string?)e.Attribute("map") == mapId)) ForgetDescendants(mapId);
    }

    internal static void RegisterIdentities(RemoteSessionMapParent owner, XElement map, string sourceMapId)
        => ClashOfRimGameComponent.RegisterRemoteMapThingIdentities(owner, map.Element("uniqueID")!.Value,
            map.Descendants("clashOfRimProjectedThingId").Select(id => new RemoteMapProjectedThingIdentity(id.Value,
                id.Parent!.Element("clashOfRimOriginalThingId")!.Value)), sourceMapId);
}
