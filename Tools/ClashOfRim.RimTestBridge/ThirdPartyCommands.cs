using HarmonyLib;
using System.Collections;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed class ThirdPartyCommands
{
    internal ThirdPartyCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        router.Register("clash.thirdParty.runtime", "Observe loaded third-party runtime types and installed compatibility patches; does not invoke adapters.", "observe", _ => new
        {
            types = new[] { "AdaptiveStorage.ThingClass", "Vehicles.VehiclePawn", "AM.AnimationManager",
                "VEF.VFEGlobal", "MVCF.Utilities.PawnVerbUtility", "MVCF.VerbManager", "AlienRace.HARStatueContainer",
                "FacialAnimation.HarmonyPatches+ExposableString" }.Select(name =>
            {
                var type = AccessTools.TypeByName(name);
                return new { name, loaded = type is not null, assembly = type?.Assembly.FullName, path = type?.Assembly.Location };
            }).ToArray(),
            patches = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains("AIRsLight.ClashOfRim.ThirdPartyCompat") == true)
                .Select(m => m.DeclaringType?.FullName + "." + m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray()
        });
        router.Register("clash.thirdParty.statues", "Observe statue display pawns and alien appearance independently of transfer metadata.", "observe", a => new
        {
            items = StatueThings(BridgeMaps.Select(a)).Where(t => t.GetInnerIfMinified().TryGetComp<CompStatue>() is not null)
                .Select(t => new { reference = objects.Add(t), data = ThirdPartyObservations.Statue(t.GetInnerIfMinified()) }).ToArray()
        });
        router.Register("clash.fixture.alienStatue", "FIXTURE: generate an installed alien pawn and its ordinary CompStatue for cross-player item tests.", "fixture", a =>
        {
            policy.RequireFixture(a); var map = BridgeMaps.Select(a);
            var kind = DefDatabase<PawnKindDef>.GetNamed("RatkinColonist");
            var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            pawn.Name = new NameSingle("RimTest alien statue subject");
            var crafter = map.mapPawns.FreeColonistsSpawned.First();
            GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(crafter.Position, map, 4), map);
            pawn.drafter.Drafted = true;
            var items = new List<object>();
            foreach (var subject in new[] { pawn, crafter })
            {
                // Headless runs do not render a portrait, which normally initializes
                // FA controllers before the native statue snapshot reads their defs.
                foreach (var comp in ThirdPartyObservations.FacialControllers(subject))
                    AccessTools.Method(comp.GetType(), "InitializeIfNeed").Invoke(comp, null);
                var statue = CompStatue.GenerateSpecificStatue(crafter, subject);
                statue.TryGetComp<CompQuality>().SetQuality(QualityCategory.Masterwork, ArtGenerationContext.Colony);
                var item = statue.MakeMinified();
                if (!GenPlace.TryPlaceThing(item, crafter.Position, map, ThingPlaceMode.Near)) throw new CommandException("fixture_failed", "Cannot place statue.");
                items.Add(new { reference = objects.Add(item), data = ThirdPartyObservations.Statue(statue) });
            }
            return new { fixture = true, items };
        });
        router.Register("clash.fixture.thirdPartyProbe", "FIXTURE: install read-only MVCF call observation for subsequent real pawn transfers.", "fixture", a =>
        {
            policy.RequireFixture(a);
            ThirdPartyObservations.Install();
            return new { fixture = true, features = ((IEnumerable)AccessTools.Field(AccessTools.TypeByName("MVCF.MVCF"), "EnabledFeatures").GetValue(null)!).Cast<string>().ToArray() };
        });
        router.Register("clash.thirdParty.pawn", "Observe existing MVCF manager, verb ownership and restore calls without creating a manager.", "observe", a =>
        {
            var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Single(p => p.ThingID == a.String("thingId"));
            return ThirdPartyObservations.Pawn(pawn);
        });
    }
    private static IEnumerable<Thing> StatueThings(Map map) => map.listerThings.AllThings
        .Concat(map.mapPawns.AllPawnsSpawned.Select(p => p.carryTracker?.CarriedThing).Where(t => t is not null)!)
        .Distinct();
}
