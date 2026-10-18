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
    private static readonly List<Pawn> generated = new();
    private static readonly HashSet<Pawn> discarded = new();
    private static int failGenerationAt;
    private static bool generatedWithGenes;

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
            // Assign the engine field without loading the game's mod catalog in the isolated fixture.
            harmony.Patch(AccessTools.Method(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.SetXenotypeDirect)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(SetXenotype)));
            harmony.Patch(AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(GeneratePawn)));
            harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Verse.Pawn.Discard)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(DiscardPawn)));
            harmony.Patch(AccessTools.Method(typeof(WorldPawns), nameof(RimWorld.Planet.WorldPawns.PassToWorld)),
                prefix: new HarmonyMethod(typeof(BiotechItemCapabilityTests), nameof(RetainPawn)));
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                world = new WorldPawns();
                player = new Faction { def = new FactionDef { defName = "BiotechTestPlayer", isPlayer = true } };
                SourceCaptureAndReuse();
                FailedRestorationPreservesExistingState();
                PreparedPlaceholdersHaveSingleOwnership();
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
            world = null!; player = null!; generated.Clear(); discarded.Clear();
        }
    }

    private static void SourceCaptureAndReuse()
    {
        string owner = "alice";
        int ownerReads = 0;
        var sources = new BiotechReproductiveSources(() => { ownerReads++; return owner; });
        var items = new BiotechThingReferenceCompatibility(() => true, sources);
        Check(sources.Capture(new Thing()).Count == 0
            && sources.TryPrepare(new ModThingReferenceDto(), new Thing(), out _, out _) && ownerReads == 0,
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
        Check(!BiotechSourcePawnRestorer.TryPrepare(invalid, null, out var absent, out missing)
            && absent is null && missing == "MissingBiotechRace", "Missing race did not reject placeholder generation with its definition name.");
        targetComp.pawnSources.Clear();
        items.AppendThingReferenceMetadata(target, transfer);
        Check(!transfer.Metadata.Keys.Any(key => key.StartsWith(BiotechThingMetadataKeys.ReproductiveSourcePrefix, StringComparison.Ordinal)),
            "Capturing an empty carrier left stale source metadata.");
    }

    private static void FailedRestorationPreservesExistingState()
    {
        var failures = new List<string>();
        var replacement = new GeneDef { defName = "BiotechFailureReplacement" };
        if (DefDatabase<GeneDef>.GetNamedSilentFail(replacement.defName) is null) DefDatabase<GeneDef>.Add(replacement);
        foreach (string scenario in new[] { "second-source", "missing-xenotype", "missing-endogene", "missing-xenogene", "item-and-source" })
        {
            world = new WorldPawns();
            var existing = Pawn("Existing", 27, Gender.Male);
            BiotechSourcePawnIdentity.MarkGlobalId(existing, "owner:alice/pawn:27");
            ((HashSet<Pawn>)AccessTools.Field(typeof(WorldPawns), "pawnsAlive").GetValue(world)!).Add(existing);
            var originalGene = new Gene { def = new GeneDef { defName = "OriginalSourceGene" }, pawn = existing };
            if (scenario is "missing-xenotype" or "missing-endogene" or "missing-xenogene")
            {
                existing.genes = new Pawn_GeneTracker(existing);
                existing.genes.Endogenes.Add(originalGene);
            }

            var target = Carrier(out var comp);
            GeneSet? originalSet = null;
            if (scenario == "item-and-source")
            {
                var embryo = new HumanEmbryo { def = new ThingDef { defName = "TestEmbryo", thingClass = typeof(HumanEmbryo) } };
                originalSet = new GeneSet(); originalSet.AddGene(originalGene.def);
                Set(embryo, "geneSet", originalSet);
                comp = new CompHasPawnSources { parent = embryo };
                Set(embryo, "comps", new List<ThingComp> { comp });
                target = embryo;
            }
            comp.AddSource(existing);
            var originalSources = comp.pawnSources.ToArray();
            var reference = new ModThingReferenceDto();
            bool secondInvalid = scenario is "second-source" or "item-and-source";
            reference.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = secondInvalid ? "2" : "1";
            BiotechThingMetadata.WriteReproductiveSourceMetadata(reference, 0,
                new ReproductiveSourceRecord("Mother", "owner:alice/pawn:27", "alice", "Changed", null, null, "Female", false, null,
                    scenario == "missing-xenotype" ? "MissingFailureXenotype" : null,
                    scenario == "missing-endogene" ? new[] { "MissingFailureGene" } : scenario == "missing-xenogene" ? new[] { replacement.defName } : null,
                    scenario == "missing-xenogene" ? new[] { "MissingFailureXenogene" } : null));
            if (secondInvalid)
                BiotechThingMetadata.WriteReproductiveSourceMetadata(reference, 1,
                    new ReproductiveSourceRecord(null, null, null, null, "MissingFailureRace", null, null, false, null, null, null, null));
            if (originalSet is not null) BiotechThingMetadata.SetGeneDefNames(reference, new[] { replacement.defName });

            var items = new BiotechThingReferenceCompatibility(() => true, new BiotechReproductiveSources(() => "alice"));
            bool result = items.TryApplyThingReferenceMetadata(reference, target, out var missing);
            string expected = scenario switch
            {
                "missing-xenotype" => "MissingFailureXenotype",
                "missing-endogene" => "MissingFailureGene",
                "missing-xenogene" => "MissingFailureXenogene",
                _ => "MissingFailureRace"
            };
            if (result || missing != expected || existing.gender != Gender.Male || !comp.pawnSources.SequenceEqual(originalSources)
                || (existing.genes is not null && !existing.genes.Endogenes.SequenceEqual(new[] { originalGene }))
                || (originalSet is not null && !ReferenceEquals(((HumanEmbryo)target).GeneSet, originalSet)))
                failures.Add(scenario + ": result=" + result + ", missing=" + missing + ", gender=" + existing.gender
                    + ", sourceCount=" + comp.pawnSources.Count + ", genes=" + existing.genes?.Endogenes.Count
                    + ", itemUnchanged=" + (originalSet is null || ReferenceEquals(((HumanEmbryo)target).GeneSet, originalSet)));
        }
        Check(failures.Count == 0, "Rejected source restoration changed existing state: " + string.Join("; ", failures));
    }

    private static void PreparedPlaceholdersHaveSingleOwnership()
    {
        var race = DefDatabase<ThingDef>.GetNamedSilentFail("PreparedSourceRace");
        if (race is null) { race = new ThingDef { defName = "PreparedSourceRace", race = new RaceProperties() }; DefDatabase<ThingDef>.Add(race); }
        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("PreparedSourceKind");
        if (kind is null) { kind = new PawnKindDef { defName = "PreparedSourceKind", race = race }; DefDatabase<PawnKindDef>.Add(kind); }
        foreach (string scenario in new[] { "generation-failure", "generated-gene-failure", "shared-new-identity", "abandoned-preparation" })
        {
            world = new WorldPawns(); generated.Clear(); discarded.Clear();
            failGenerationAt = scenario == "generation-failure" ? 2 : 0;
            generatedWithGenes = scenario == "generated-gene-failure";
            var existing = Pawn("Original", 47, Gender.Male);
            BiotechSourcePawnIdentity.MarkGlobalId(existing, "owner:alice/pawn:47");
            ((HashSet<Pawn>)AccessTools.Field(typeof(WorldPawns), "pawnsAlive").GetValue(world)!).Add(existing);
            var target = Carrier(out var comp); comp.AddSource(existing);
            var reference = new ModThingReferenceDto();
            bool shared = scenario == "shared-new-identity";
            reference.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "3";
            BiotechThingMetadata.WriteReproductiveSourceMetadata(reference, 0,
                new ReproductiveSourceRecord(null, "owner:alice/pawn:47", "alice", null, null, null, "Female", false, null, null, null, null));
            for (int i = 1; i <= 2; i++)
                BiotechThingMetadata.WriteReproductiveSourceMetadata(reference, i,
                    new ReproductiveSourceRecord(null, "owner:alice/pawn:new" + (shared ? 1 : i), "alice", "New",
                        shared && i == 2 ? "UnneededRaceForSharedPawn" : race.defName, kind.defName, "Female", false, null, null,
                        generatedWithGenes && i == 2 ? new[] { "MissingGeneratedGene" } : null, null));
            var sources = new BiotechReproductiveSources(() => "alice");
            if (scenario == "abandoned-preparation")
            {
                Check(sources.TryPrepare(reference, target, out var pending, out _) && pending is not null, "Valid source preparation failed.");
                pending!.Dispose(); pending.Dispose();
                Check(discarded.Count == 2 && discarded.SetEquals(generated) && comp.pawnSources.Single() == existing
                    && existing.gender == Gender.Male && world.AllPawnsAliveOrDead.Count() == 1,
                    "Abandoned preparation leaked placeholders, discarded an existing pawn or applied pending state.");
                continue;
            }
            var items = new BiotechThingReferenceCompatibility(() => true, sources);
            bool result = items.TryApplyThingReferenceMetadata(reference, target, out string? missing);
            if (shared)
            {
                Check(result && missing is null && generated.Count == 1 && discarded.Count == 0 && existing.gender == Gender.Female
                    && comp.pawnSources.Contains(existing) && comp.pawnSources.Contains(generated[0]) && world.AllPawnsAliveOrDead.Count() == 2,
                    "Shared new source identity generated more than one pawn or released committed state.");
            }
            else
            {
                Check(!result && missing == (generatedWithGenes ? "MissingGeneratedGene" : kind.defName)
                    && generated.Count == (generatedWithGenes ? 2 : 1) && discarded.SetEquals(generated)
                    && existing.gender == Gender.Male && comp.pawnSources.Single() == existing && world.AllPawnsAliveOrDead.Count() == 1,
                    "Failed placeholder preparation changed existing state or leaked newly generated pawns: " + scenario);
            }
        }
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
    private static void Set(object target, string field, object? value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool SetXenotype(Pawn_GeneTracker __instance, XenotypeDef? xenotype) { Set(__instance, "xenotype", xenotype); return false; }
    private static bool GeneratePawn(PawnGenerationRequest __0, ref Pawn __result)
    {
        Check(__0.ForceGenerateNewPawn && !__0.CanGeneratePawnRelations, "Placeholder generation may reuse a live pawn or create unrelated relations.");
        if (failGenerationAt == generated.Count + 1) throw new InvalidOperationException("injected source generation failure");
        __result = Pawn("Generated", 80 + generated.Count, Gender.None);
        __result.def = __0.KindDef.race; __result.kindDef = __0.KindDef;
        if (generatedWithGenes) __result.genes = new Pawn_GeneTracker(__result);
        generated.Add(__result);
        return false;
    }
    private static bool DiscardPawn(Pawn __instance) { Check(discarded.Add(__instance), "Placeholder discarded twice."); return false; }
    private static bool RetainPawn(WorldPawns __instance, Pawn __0, PawnDiscardDecideMode __1)
    {
        Check(__1 == PawnDiscardDecideMode.KeepForever, "Prepared source lost its retention policy.");
        ((HashSet<Pawn>)AccessTools.Field(typeof(WorldPawns), "pawnsAlive").GetValue(__instance)!).Add(__0);
        return false;
    }
    private static bool WorldPawns(ref WorldPawns __result) { __result = world; return false; }
    private static bool WorldObjects(ref WorldObjectsHolder? __result) { __result = null; return false; }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
