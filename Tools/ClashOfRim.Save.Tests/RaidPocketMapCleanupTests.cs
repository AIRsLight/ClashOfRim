using System.Xml.Linq;
using AIRsLight.ClashOfRim.Save;

internal static class RaidPocketMapCleanupTests
{
    internal static void Run()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RaidCleanup.xml"));
        var game = doc.Root!.Element("game")!; var maps = game.Element("maps")!;
        var battle = maps.Elements("li").Single(m => m.Element("uniqueID")!.Value != "0");
        string battleId = battle.Element("uniqueID")!.Value;
        var child = Map(90, 900); var nested = Map(91, 901); var homeChild = Map(92, 902);
        foreach (string id in new[] { "raid-attacker", "raid-support" })
        {
            var pawn = battle.Element("things")!.Elements("thing").Single(t => t.Element("id")?.Value == id);
            pawn.Remove(); nested.Element("things")!.Add(pawn); pawn.SetElementValue("map", 91);
        }
        maps.Add(child, nested, homeChild);
        game.Element("world")!.Add(new XElement("pocketMaps", Parent(901, "@Map_90"), Parent(900, "Map_" + battleId), Parent(902, "Map_0")));
        string path = Path.Combine(Path.GetTempPath(), "clash-pocket-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            doc.Save(path);
            var package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity("attacker", "colony-a", "pocket-test"), DateTimeOffset.UnixEpoch, SnapshotPayloadEncoding.RawRws);
            if (!RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(package, "clean", DateTimeOffset.UnixEpoch.AddMinutes(1), out var clean, out RaidAttackerSnapshotCleanupResult result)
                || result.RemovedMapCount != 3 || result.LostAttackPawns.Count != 1 || result.LostSupportPawns.Count != 1)
                throw new Exception("Nested battlefield cleanup omitted maps or attack/support losses.");
            using var bytes = new MemoryStream(clean.Payload); var saved = XDocument.Load(bytes);
            if (saved.Descendants("pocketMaps").Elements("li").Single().Element("ID")!.Value != "902"
                || clean.Index.Maps.Count != 2 || clean.Index.Pawns.Single(p => p.LocalId == "raid-attacker").MapUniqueId is not null)
                throw new Exception("Nested cleanup left dangling pockets, lost home pockets or failed to move the attacker into world pawns.");
        }
        finally { File.Delete(path); }
    }
    private static XElement Map(int id, int parent) => new("li", new XElement("uniqueID", id), new XElement("mapInfo", new XElement("parent", "WorldObject_" + parent)), new XElement("things"));
    private static XElement Parent(int id, string source) => new("li", new XElement("ID", id), new XElement("sourceMap", source));
}
