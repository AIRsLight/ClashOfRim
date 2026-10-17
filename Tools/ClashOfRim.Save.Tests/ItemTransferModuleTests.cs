using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;

internal static class ItemTransferModuleTests
{
    internal static void Run()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                RoundTripAndStore();
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static void RoundTripAndStore()
    {
        var reference = new CrossMapPawnReference("pawn-é", "base", "名字", false, "Player",
            new Dictionary<string, string?> { ["origin"] = "İ" });
        var equipment = new PawnExchangeEquipmentItem("weapon", "Gun", "名字", 1, "Good", 55, false,
            true, reference.GlobalId, true, "独特", ["trait"]);
        var pawn = new PawnExchangePackage(SafePawnExchangeSerializer.CurrentPackageVersion, reference,
            new("Human", "Colonist", "Player", "Female"), new("名字", "Thin", "Head", "Hair", null, "#ffffff", "#000000"),
            new(false, 120000, 130000, null, "health"), [equipment], [equipment], [new("other", "他", false, "Sibling")],
            new("<pawn><def>Human</def></pawn>", null, [new("Pawn_2", "other-placeholder", reference)]),
            [new("provider", "kind", new() { ["key"] = "value" }, "{\"v\":1}")]);
        byte[] xml = Encoding.UTF8.GetBytes("<thing><def>Gun</def><comps><state>复杂</state></comps></thing>");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(xml);
        var thing = new ThingStatePackage(1, "gun", "Gun", "复杂", 1,
            new(Convert.ToBase64String(compressed.ToArray()), null, xml.Length));
        var entries = new[]
        {
            new EventThingReference("pawn-é", "Human", 1, "paid", UniqueWeaponTraits: [], PawnPackage: pawn,
                Metadata: new() { ["custom"] = "中文" }),
            new EventThingReference("gun", "Gun", 1, "paid", "Good", 55, "Inner", "Steel", "Normal", 33,
                true, true, "名字", "pawn-é", "复杂", 45.25f, true, ["trait"], StuffDefName: "Steel", MaxHitPoints: 100,
                MinifiedInnerMaxHitPoints: 80, Metadata: new() { ["custom"] = "İ" }, ThingPackage: thing)
        };
        var dtos = entries.Select(ToThingReferenceDto).ToArray();
        for (int i = 0; i < entries.Length; i++)
        {
            Require(JsonSerializer.Serialize(ToEventThingReference(dtos[i], "paid")) == JsonSerializer.Serialize(entries[i]),
                "Transfer codec lost nested payload or display fields.");
            Require(!ReferenceEquals(entries[i].Metadata, dtos[i].Metadata), "Transfer metadata aliases its source dictionary.");
        }
        var pawns = new PawnPackageRegistry();
        var things = new ThingPackageRegistry();
        var packages = new ItemTransferPackages(pawns, things);
        Require(packages.TryStore(new("userA", "colonyA", "paid"), "operation", dtos, out var stored, out var failure),
            "Transfer storage failed: " + failure?.Message);
        Require(packages.TryStore(new("userA", "colonyA", "paid"), "operation", dtos, out var repeated, out _)
            && stored[0].PawnPackageId == repeated[0].PawnPackageId && stored[1].ThingPackageId == repeated[1].ThingPackageId,
            "Transfer storage duplicated package IDs on retry.");
        Require(stored[0].PawnPackage is null && stored[1].ThingPackage is null
            && stored[0].Metadata!["clashofrim.pawn.thingDef"] == "Human"
            && stored[0].Metadata["clashofrim.pawn.biologicalAgeTicks"] == "120000",
            "Stored transfer did not replace inline packages or preserve summary metadata.");
        Require(pawns.TryGetPackage(stored[0].PawnPackageId!, out var reloadedPawn, out _)
            && JsonSerializer.Serialize(pawn) == JsonSerializer.Serialize(reloadedPawn), "Stored pawn lost complex state.");
        Require(things.TryGetPackage(stored[1].ThingPackageId!, out var reloadedThing, out _)
            && SafeThingStatePackageSerializer.DecompressXml(reloadedThing!.Scribe) == Encoding.UTF8.GetString(xml),
            "Stored thing lost scribe state.");
        var invalid = new ThingReferenceDto("bad", "Gun", 1,
            thingPackage: new ThingStatePackageDto(1, "bad", "Gun", "bad", 1, new("invalid", null, 10), null));
        Require(!packages.TryStore(new("userA", "colonyA", "paid"), "bad", [invalid], out var rejected, out failure)
            && rejected.Count == 0 && failure?.ErrorCode == ProtocolErrorCode.ValidationFailed,
            "Invalid complex payload reached storage.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
