using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class BiotechItemCapabilityTests
{
    private static WorldPawns world = null!;
    private static Faction player = null!;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.BiotechItemCapabilities");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Bind engine globals to an isolated world; capture, identity, matching and restoration stay real.
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WorldPawns)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(WorldPawns)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WorldObjects)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(WorldObjects)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(Player)));
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                world = new WorldPawns();
                player = new Faction { def = new FactionDef { defName = "BiotechTestPlayer", isPlayer = true } };
                SourceCaptureAndReuse();
                SharedGeneCacheInvalidation();
                RegisteredItemCapabilities();
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
            world = null!; player = null!;
        }
    }

    private static void SourceCaptureAndReuse()
    {
        string owner = "alice";
        int ownerReads = 0;
        var sources = new BiotechReproductiveSources(() => { ownerReads++; return owner; });
        var items = new BiotechThingReferenceCompatibility(() => true, sources);
        Check(sources.Capture(new Thing()).Count == 0
            && sources.TryApply(new ModThingReferenceDto(), new Thing(), out _) && ownerReads == 0,
            "Items without reproductive sources unnecessarily read session identity.");
        var mother = Pawn("Mother", 1, Gender.Female);
        var father = Pawn("Father", 2, Gender.Male);
        mother.questTags = new List<string> { "unrelated", "ClashOfRim.ReproductiveSourcePlaceholder: owner:original/pawn:7 " };
        var carrier = Carrier(out var comp);
        comp.pawnSources = new List<Pawn> { mother, mother, null!, father, Pawn("Third", 3, Gender.None), Pawn("Fourth", 4, Gender.None), Pawn("Fifth", 5, Gender.None) };
        var dto = new ModThingReferenceDto();
        items.AppendThingReferenceMetadata(carrier, dto);
        var captured = BiotechThingMetadata.ReproductiveSources(dto);
        Check(ownerReads == 1 && captured.Count == 4 && captured[0].Role == "Mother" && captured[1].Role == "Father"
            && captured[0].Name == "Mother" && captured[0].GlobalId == "owner:original/pawn:7" && captured[0].OwnerUserId == "original"
            && captured[1].OwnerUserId == "alice", "Capture lost source ordering, identity, owner or four-source deduplication.");
        Check(items.ThingReferenceMatches(dto, carrier), "Captured sources do not match the same live carrier.");
        int readsBeforeDisabled = ownerReads;
        var disabled = new BiotechThingReferenceCompatibility(() => false, sources);
        disabled.AppendThingReferenceMetadata(carrier, new ModThingReferenceDto());
        Check(disabled.TryApplyThingReferenceMetadata(dto, carrier, out _) && ownerReads == readsBeforeDisabled,
            "Disabled item capabilities still accessed source identity.");
        owner = "bob";
        Check(sources.Capture(carrier)[1].OwnerUserId == "bob" && sources.Capture(carrier)[0].OwnerUserId == "original",
            "Source capture cached the previous session owner or re-keyed a foreign source.");

        var known = Pawn("Known", 8, Gender.Female);
        BiotechSourcePawnIdentity.MarkGlobalId(known, "owner:bob/pawn:8");
        BiotechSourcePawnIdentity.MarkGlobalId(known, "owner:bob/pawn:8");
        Check(known.questTags.Count == 1, "Repeated identity marking duplicated tags.");
        ((HashSet<Pawn>)AccessTools.Field(typeof(WorldPawns), "pawnsAlive").GetValue(world)!).Add(known);
        var target = Carrier(out var targetComp);
        var transfer = new ModThingReferenceDto();
        transfer.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "1";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(transfer, 0, BiotechReproductiveSources.CapturePawn(known, owner));
        Check(items.TryApplyThingReferenceMetadata(transfer, target, out var missing) && missing is null
            && ReferenceEquals(targetComp.pawnSources.Single(), known), "Source restoration failed to reuse the globally identified world pawn.");
        Check(items.TryApplyThingReferenceMetadata(transfer, target, out _) && targetComp.pawnSources.Count == 1
            && world.AllPawnsAliveOrDead.Count() == 1, "Repeated source restoration duplicated a pawn or carrier link.");

        var invalid = new ReproductiveSourceRecord(null, null, null, null, "MissingBiotechRace", null, null, false, null, null, null, null);
        Check(!BiotechSourcePawnRestorer.TryFindOrCreate(invalid, owner, out var absent, out missing)
            && absent is null && missing == "MissingBiotechRace", "Missing race did not reject placeholder generation with its definition name.");
        targetComp.pawnSources.Clear();
        items.AppendThingReferenceMetadata(target, transfer);
        Check(!transfer.Metadata.Keys.Any(key => key.StartsWith(BiotechThingMetadataKeys.ReproductiveSourcePrefix, StringComparison.Ordinal)),
            "Capturing an empty carrier left stale source metadata.");
    }

    private static void SharedGeneCacheInvalidation()
    {
        var pawn = Pawn("Genes", 9, Gender.None);
        pawn.genes = new Pawn_GeneTracker(pawn);
        var cachedGenes = new List<Gene>();
        Set(pawn.genes, "cachedGenes", cachedGenes);
        Set(pawn.genes, "cachedHasCustomXenotype", true);
        Set(pawn.genes, "cachedCustomXenotype", new CustomXenotype());
        Set(pawn.genes, "hasCachedWaterCost", true);
        BiotechGeneRuntime.ResetCustomXenotypeCaches(pawn);
        Check(Get(pawn.genes, "cachedHasCustomXenotype") is null && Get(pawn.genes, "cachedCustomXenotype") is null
            && ReferenceEquals(Get(pawn.genes, "cachedGenes"), cachedGenes), "Custom-xenotype invalidation changed unrelated gene caches.");
        BiotechGeneRuntime.ResetTrackerCaches(pawn);
        Check(Get(pawn.genes, "cachedGenes") is null && Equals(Get(pawn.genes, "hasCachedWaterCost"), false),
            "Shared source/map gene invalidation retained a cached gene list or water-cost flag.");
        pawn.genes = null;
        BiotechGeneRuntime.ResetTrackerCaches(pawn);
    }

    private static void RegisteredItemCapabilities()
    {
        ClashOfRimCompatibilityApi.BeginRegistrationCycle("biotech-items");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(BiotechCompatibilityKeys.PackageId, () => true, new[] { BiotechCompatibilityKeys.TradeMetadata });
        BiotechCompatibilityPackage.RegisterItemCapabilities();
        BiotechCompatibilityPackage.RegisterItemCapabilities();
        var geneDef = new ThingDef { thingClass = typeof(Genepack) };
        var dto = new ModThingReferenceDto();
        Check(ClashOfRimCompatibilityApi.IsThingReferenceUniqueRequest(geneDef)
            && ClashOfRimCompatibilityApi.SuppressesStandardThingStats(geneDef)
            && !ClashOfRimCompatibilityApi.IsThingReferenceComplete(ThingReferenceSurfaces.TradeRequest, geneDef, dto),
            "The package did not register the extracted item classifier/completeness/display components.");
        BiotechThingMetadata.SetTargetGeneDefNames(dto, new[] { "A" });
        Check(ClashOfRimCompatibilityApi.IsThingReferenceComplete(ThingReferenceSurfaces.TradeRequest, geneDef, dto)
            && ClashOfRimCompatibilityApi.ThingReferenceMetadataStrictness(dto) == 1_000_000
            && !ClashOfRimCompatibilityApi.ThingReferenceMetadataMatches(dto, new ModThingReferenceDto())
            && ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(dto).Count() == 4,
            "Package registration lost matching/strictness or duplicated the display cache provider.");
        ClashOfRimCompatibilityApi.ClearThingReferenceMetadata(ThingReferenceSurfaces.TradeRequest, new ThingDef { thingClass = typeof(Thing) }, dto);
        Check(dto.Metadata.Count == 0, "Changing to an ordinary item retained Biotech metadata.");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(Array.Empty<string>());
        Check(!ClashOfRimCompatibilityApi.IsThingReferenceUniqueRequest(geneDef)
            && !ClashOfRimCompatibilityApi.SuppressesStandardThingStats(geneDef)
            && !ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(dto).Any(), "Registered item components ignored a changed DLC baseline.");
    }

    private static ThingWithComps Carrier(out CompHasPawnSources comp)
    {
        var thing = new ThingWithComps { def = new ThingDef { defName = "TestCarrier", thingClass = typeof(ThingWithComps) } };
        comp = new CompHasPawnSources { parent = thing };
        Set(thing, "comps", new List<ThingComp> { comp });
        return thing;
    }

    private static Pawn Pawn(string name, int id, Gender gender)
    {
        var pawn = new Pawn { def = new ThingDef { defName = "TestHuman", race = new RaceProperties() }, thingIDNumber = id, Name = new NameSingle(name), gender = gender };
        pawn.health = new Pawn_HealthTracker(pawn);
        Set(pawn, "factionInt", player);
        return pawn;
    }

    private static object? Get(object target, string field) => AccessTools.Field(target.GetType(), field).GetValue(target);
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool WorldPawns(ref WorldPawns __result) { __result = world; return false; }
    private static bool WorldObjects(ref WorldObjectsHolder? __result) { __result = null; return false; }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
