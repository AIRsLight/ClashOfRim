using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class IdeologyMetadataTests
{
    public static void Run()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var complete = JsonNode.Parse("""
            {"globalKey":"owner:alice/ideo:7","ownerUserId":"alice","ownerColonyId":"home","localId":"7",
             "name":"Test ideology","culture":"Rural","cultureLabel":"乡村","cultureIconPath":"Cultures/Rural",
             "factionDefName":"Outlander","iconDefName":"Sun","iconPath":"Icons/Sun","colorDefName":"Red","colorHex":"#FF0000",
             "primaryFactionColor":"(1,0,0,1)","primaryFactionColorHex":"#FF0000","savedIdeoPackageXml":"<saveable/>","savedIdeoPackageSha256":"package",
             "updatedAtGameTicks":0,"preceptDefNames":["Funeral","IdeoRelic","IdeoRelic"],"preceptCount":3}
            """)!;
        WorldIdeoSummaryDto Read(JsonNode value) => value.Deserialize<WorldIdeoSummaryDto>(options)!;
        var existing = Read(complete);
        var summary = complete.DeepClone();
        foreach (string field in new[] { "cultureLabel", "cultureIconPath", "factionDefName", "iconPath", "colorHex", "primaryFactionColorHex", "savedIdeoPackageXml", "savedIdeoPackageSha256", "updatedAtGameTicks" })
            summary[field] = null;
        WorldIdeoSummaryDto Merge(JsonNode incoming) => IdeologyServerCompatibility.MergeWorldIdeos([existing], [Read(incoming)], "alice", "home").Single();
        var merged = Merge(summary);
        if (JsonSerializer.Serialize(merged, options) != JsonSerializer.Serialize(existing, options))
            throw new Exception("Summary overwrote persisted ideology metadata/package.");
        if (merged.PreceptDefNames.Count != 2 || merged.PreceptCount != 3 || merged.UpdatedAtGameTicks != 0)
            throw new Exception("Def catalog multiplicity or default tick semantics changed.");
        // Rich display metadata is still valuable when no complete XML was available.
        complete["savedIdeoPackageXml"] = null; complete["savedIdeoPackageSha256"] = null;
        existing = Read(complete);
        merged = Merge(summary);
        if (merged.CultureLabel != "乡村" || merged.IconPath != "Icons/Sun")
            throw new Exception("Metadata without a full XML package was discarded.");
        // Changing canonical Defs must not inherit presentation for the old Defs.
        summary["culture"] = "Urban"; summary["iconDefName"] = "Moon"; summary["colorDefName"] = "Blue";
        merged = Merge(summary);
        if (merged.CultureLabel is not null || merged.CultureIconPath is not null || merged.IconPath is not null || merged.ColorHex is not null)
            throw new Exception("Changed ideology inherited stale Def presentation.");
        summary["cultureLabel"] = "新文化";
        if (Merge(summary).CultureLabel != "新文化") throw new Exception("Incoming metadata was ignored.");
        summary["savedIdeoPackageXml"] = "<replacement/>";
        if (Merge(summary).FactionDefName is not null || Merge(summary).SavedIdeoPackageXml != "<replacement/>")
            throw new Exception("Complete replacement package was treated as a partial summary.");
        var extension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension([existing])!;
        var reloaded = IdeologyServerCompatibility.ReadWorldIdeoCatalog([extension]).Single();
        if (JsonSerializer.Serialize(reloaded, options) != JsonSerializer.Serialize(existing, options))
            throw new Exception("Persisted metadata changed during catalog roundtrip.");

        complete["ownerUserId"] = "server"; complete["globalKey"] = "owner:server/ideo:7";
        complete["factionBindings"] = JsonNode.Parse("""
            [{"factionDefName":"Tribe","factionIndex":0,"ideoIndex":0},
             {"factionDefName":"Tribe","factionIndex":1,"ideoIndex":1}]
            """);
        var server = Read(complete);
        var normalized = IdeologyServerCompatibility.NormalizeWorldIdeo(server, "alice", "home", "world");
        var serverExtension = IdeologyServerCompatibility.BuildWorldIdeoCatalogExtension([normalized])!;
        var restored = IdeologyServerCompatibility.ReadWorldIdeoCatalog([serverExtension]).Single();
        if (restored.FactionBindings is not { Count: 2 } || !restored.FactionBindings.SequenceEqual(server.FactionBindings!))
            throw new Exception("NPC primary/minor references lost in normalization or persistence.");
        complete["ownerUserId"] = "alice"; complete["globalKey"] = "owner:alice/ideo:7";
        if (Read(complete).FactionBindings is not null)
            throw new Exception("Player-owned ideology supplied authoritative NPC bindings.");
        var legacy = complete.DeepClone(); legacy.AsObject().Remove("factionBindings");
        if (Read(legacy).FactionBindings is not null)
            throw new Exception("Legacy catalog lost the distinction between absent and empty bindings.");
    }
}
