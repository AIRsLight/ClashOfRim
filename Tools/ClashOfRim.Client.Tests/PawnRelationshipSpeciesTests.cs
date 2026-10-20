using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using Verse;

internal static class PawnRelationshipSpeciesTests
{
    internal static void Run()
    {
        Payloads();
        using var things = new DefDatabaseTestScope<ThingDef>();
        using var kinds = new DefDatabaseTestScope<PawnKindDef>();
        var human = Race("Human");
        var alien = Race("TransferAlien");
        var numberedAlien = Race("TransferAlien2");
        var animal = Race("TransferAnimal");
        var orphan = Race("TransferOrphan");
        var colonist = Kind("Colonist", human);
        var alienKind = Kind("TransferAlienColonist", alien);
        var numberedKind = Kind("TransferAlien2Colonist", numberedAlien);
        var animalKind = Kind("TransferAnimalKind", animal);
        things.Replace(new[] { human, alien, numberedAlien, animal, orphan, new ThingDef { defName = "Steel" } });
        kinds.Replace(new[] { colonist, alienKind, numberedKind, animalKind });
        var previousColonist = PawnKindDefOf.Colonist;
        var previousCulture = CultureInfo.CurrentCulture;
        PawnKindDefOf.Colonist = colonist;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Same(alienKind, Stub("opaque", alien.defName, alienKind.defName));
                Same(animalKind, Stub("opaque", animal.defName, animalKind.defName, "Bond"));
                Same(alienKind, Stub("opaque", alien.defName, "RemovedKind"));
                Same(alienKind, Stub("opaque", alien.defName, animalKind.defName));
                Same(animalKind, Stub("opaque", null, animalKind.defName, "Bond"));
                Same(numberedKind, Stub("TransferAlien2123"));
                Same(numberedKind, Stub("Thing_TransferAlien2123"));
                Same(animalKind, Stub("TransferAnimal123", relation: "Bond"));
                Same(colonist, Stub("old-opaque-id"));
                Reject(Stub("opaque", "MissingRace", animalKind.defName));
                Reject(Stub("TransferOrphan123"));
                Reject(Stub("opaque", "Steel"));
                Reject(Stub("opaque", kind: "MissingKind"));
                Reject(Stub("opaque", relation: "Bond"));
                Reject(Stub("TransferAnimal", relation: "Bond"));
                Reject(Stub("TransferAnimal12x", relation: "Bond"));
            }
        }
        finally { PawnKindDefOf.Colonist = previousColonist; CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void Payloads()
    {
        const string relation = "{\"OtherPawnGlobalId\":\"owner:friend/pawn:TransferAlien123\",\"OtherPawnName\":\"朋友-é\","
            + "\"OtherPawnDead\":false,\"RelationDef\":\"Parent\",\"OtherPawnThingDef\":\"TransferAlien\",\"OtherPawnKindDef\":\"AlienColonist\"}";
        const string package = "{\"PackageVersion\":1,\"Apparel\":[],\"Equipment\":[],\"Extensions\":[],\"Relationships\":[" + relation + "]}";
        var gift = ItemDeliveryPayloadReader.Read("{\"Items\":[{\"PawnPackage\":" + package + "}]}");
        var giftPackage = (ModPawnExchangePackageDto)typeof(ItemDeliveryClientProcessor)
            .GetMethod("ToModPawnPackage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { gift.Items.Single().PawnPackage })!;
        CheckRelation(giftPackage.Relationships.Single());
        foreach (string supportPackage in new[] { package, package.Replace("\"OtherPawnThingDef\"", "\"otherPawnThingDef\"")
                     .Replace("\"OtherPawnKindDef\"", "\"otherPawnKindDef\"") })
        {
            var support = SupportPawnPayloadReader.Read("{\"pawnPackage\":" + supportPackage + "}");
            var converted = ConvertSupport(support);
            CheckRelation(converted.Relationships.Single());
            var returned = (SupportPawnPayloadSummary)typeof(SupportPawnApplicator)
                .GetMethod("FromPackage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { "pawn", converted })!;
            CheckRelation(ConvertSupport(returned).Relationships.Single());
        }
    }

    private static ModPawnExchangePackageDto ConvertSupport(SupportPawnPayloadSummary payload) =>
        (ModPawnExchangePackageDto)typeof(SupportPawnScribeRestorer)
            .GetMethod("ToPawnExchangePackage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { payload })!;

    private static void CheckRelation(ModPawnExchangeRelationshipStubDto relationship)
    {
        if (relationship.OtherPawnThingDef != "TransferAlien" || relationship.OtherPawnKindDef != "AlienColonist"
            || relationship.OtherPawnName != "朋友-é" || relationship.RelationDef != "Parent")
            throw new Exception("Gift/support conversion lost relationship identity.");
    }

    private static ThingDef Race(string name) => new() { defName = name, race = new RaceProperties() };
    private static PawnKindDef Kind(string name, ThingDef race) => new() { defName = name, race = race };
    private static ModPawnExchangeRelationshipStubDto Stub(string id, string? race = null, string? kind = null, string relation = "Parent") =>
        new() { OtherPawnGlobalId = "owner:foreign/pawn:" + id, OtherPawnThingDef = race, OtherPawnKindDef = kind, RelationDef = relation };
    private static void Same(PawnKindDef expected, ModPawnExchangeRelationshipStubDto stub)
    {
        if (!ReferenceEquals(PawnExchangeScribeRestorer.ResolveRelationshipPawnKind(stub), expected))
            throw new Exception("Relationship species changed: " + stub.OtherPawnGlobalId);
    }
    private static void Reject(ModPawnExchangeRelationshipStubDto stub)
    {
        try { PawnExchangeScribeRestorer.ResolveRelationshipPawnKind(stub); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unresolvable relationship species was replaced: " + stub.OtherPawnGlobalId + "/" + stub.OtherPawnThingDef);
    }
}
