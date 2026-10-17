using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Support;
using HarmonyLib;
using RimTest.Protocol;
using RimTest.Runtime;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

/// <summary>Independent, stable observations shared by lifecycle and transfer scenarios.
/// Fixtures are bounded, explicitly opted into per command and never reported as player actions.</summary>
internal sealed partial class DomainCommands
{
    private static readonly Type ThingUtility = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.Trades.TradeThingReferenceUtility", true)!;
    private void RegisterStability()
    {
        bridge.Router.Register("clash.stability.pawn", "Read stable pawn traits, skills, permanent health, genes, relations and gear without using the transfer serializer.", "observe", a =>
        {
            var pawn = FindPawn(a);
            return new { reference = bridge.Objects.Add(pawn), id = pawn.ThingID, data = PawnState(pawn) };
        });
        bridge.Router.Register("clash.stability.map", "Read spawned item/building contents and unique identities; no simulation is advanced.", "observe", a =>
        {
            var map = StabilityMap(a);
            var things = map.listerThings.AllThings.Where(t => t.def.category is ThingCategory.Item or ThingCategory.Building)
                .OrderBy(t => t.ThingID, StringComparer.Ordinal).Select(t => new { id = t.ThingID, x = t.Position.x, z = t.Position.z, state = ThingState(t) }).ToArray();
            return new { mapId = "Map_" + map.uniqueID, thingCount = things.Length, duplicateIds = things.GroupBy(t => t.id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray(),
                sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(things, Wire.Json))) };
        });
        bridge.Router.Register("clash.stability.items", "Independent facts for spawned items matching a definition, including minified inner items.", "observe", a =>
            ObjectInspector.Paginate(StabilityMap(a).listerThings.AllThings.Where(t => t.def.defName == a.String("defName") || (t is MinifiedThing m && m.InnerThing?.def.defName == a.String("defName")))
                .OrderBy(t => t.ThingID, StringComparer.Ordinal).Select(t => new { reference = bridge.Objects.Add(t), id = t.ThingID, data = ThingState(t) }).ToArray(), a));
        bridge.Router.Register("clash.stability.support", "Support quest lifecycle facts, including finished quests and deadlines.", "observe", a =>
            ObjectInspector.Paginate(Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashSupportPawn>())
                .Select(p => new { p.EventId, p.PermanentSupport, p.ExpiresAtGameTicks, p.Completed, p.FinishRequested, p.FinishReason }).ToArray(), a));
        bridge.Router.Register("clash.fixture.pawnState", "FIXTURE: set one existing player pawn's gear quality/durability, permanent scar, skill, sibling and deceased relationship references; requires explicit acknowledgement and debug capability.", "fixture", a =>
        {
            Fixture(a); var p = FindPawn(a);
            if (!p.Spawned || p.Faction != Faction.OfPlayer || !p.RaceProps.Humanlike) throw new CommandException("invalid_context", "Requires spawned player human.");
            var other = p.Map.mapPawns.FreeColonistsSpawned.First(q => q != p);
            if (!p.relations.DirectRelationExists(PawnRelationDefOf.Sibling, other)) p.relations.AddDirectRelation(PawnRelationDefOf.Sibling, other);
            var deceased = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            deceased.Name = new NameSingle("RimTest deceased relative");
            deceased.health.SetDead();
            Find.WorldPawns.PassToWorld(deceased, PawnDiscardDecideMode.KeepForever);
            p.relations.AddDirectRelation(PawnRelationDefOf.ExSpouse, deceased);
            p.skills.GetSkill(SkillDefOf.Shooting).Level = 12; p.skills.GetSkill(SkillDefOf.Shooting).passion = Passion.Major;
            if (p.equipment.Primary is null)
            {
                var weapon = p.Map.listerThings.AllThings.OfType<ThingWithComps>().First(t => t.def.IsWeapon);
                weapon.DeSpawn(); p.equipment.AddEquipment(weapon);
            }
            if (ModsConfig.BiotechActive) p.genes.AddGene(DefDatabase<GeneDef>.GetNamed("MoveSpeed_Quick"), true);
            foreach (var item in p.equipment.AllEquipmentListForReading.Cast<Thing>().Concat(p.apparel.WornApparel))
            {
                item.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
                if (item.def.useHitPoints) item.HitPoints = Math.Max(1, item.MaxHitPoints * 3 / 4);
            }
            var part = p.RaceProps.body.AllParts.First(b => b.def == BodyPartDefOf.Torso);
            var scar = HediffMaker.MakeHediff(HediffDefOf.Cut, p, part);
            scar.Severity = 2; scar.TryGetComp<HediffComp_GetsPermanent>().IsPermanent = true;
            p.health.AddHediff(scar);
            return new { fixture = true, pawn = bridge.Objects.Add(p), data = PawnState(p) };
        });
        bridge.Router.Register("clash.fixture.supportDue", "FIXTURE: advance one active temporary support quest deadline to the next game tick; does not run or complete the quest.", "fixture", a =>
        {
            Fixture(a);
            var p = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashSupportPawn>())
                .Single(p => p.EventId == a.String("eventId") && !p.Completed && !p.PermanentSupport && !p.FinishRequested);
            long original = p.ExpiresAtGameTicks; p.ExpiresAtGameTicks = Find.TickManager.TicksGame + 1;
            return new { fixture = true, original, p.ExpiresAtGameTicks };
        });
        bridge.Router.Register("clash.fixture.supportDeath", "FIXTURE: kill one active borrowed pawn through the game's death path; requires explicit acknowledgement.", "fixture", a =>
        {
            Fixture(a); var pawn = FindPawn(a);
            if (!pawn.Spawned || pawn.Dead || AccessTools.Method(typeof(ClashOfRimGameComponent), "FindSupportAssignment").Invoke(null, new object[] { pawn }) is null)
                throw new CommandException("invalid_context", "Requires a living spawned support pawn.");
            pawn.Kill(null);
            return new { fixture = true, pawn.Dead, pawn.ThingID };
        });
        bridge.Router.Register("clash.fixture.complexItems", "FIXTURE: create six ordinary game items with non-default quality, durability, taint, ingredients, rot, color and fuel for real trade transfer tests.", "fixture", a =>
        {
            Fixture(a); var map = StabilityMap(a); var cell = map.mapPawns.FreeColonistsSpawned.First().Position;
            var result = new List<object>();
            foreach (var (name, material) in new[] { ("Apparel_Duster", "Cloth"), ("Gun_BoltActionRifle", ""), ("MealFine", ""), ("Bed", "WoodLog"), ("SculptureSmall", "BlocksMarble"), ("FueledStove", "") })
            {
                var def = DefDatabase<ThingDef>.GetNamed(name);
                Thing t = ThingMaker.MakeThing(def, string.IsNullOrEmpty(material) ? null : DefDatabase<ThingDef>.GetNamed(material));
                t.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Masterwork, ArtGenerationContext.Colony);
                if (t.def.useHitPoints) t.HitPoints = Math.Max(1, t.MaxHitPoints * 73 / 100);
                if (t is Apparel apparel) apparel.WornByCorpse = true;
                t.TryGetComp<CompColorable>()?.SetColor(new UnityEngine.Color(0.2f, 0.4f, 0.6f));
                if (t.TryGetComp<CompIngredients>() is { } ingredients) { ingredients.RegisterIngredient(ThingDefOf.RawPotatoes); ingredients.RegisterIngredient(ThingDefOf.Human.race.meatDef); }
                if (t.TryGetComp<CompRottable>() is { } rot) rot.RotProgress = 12000;
                if (t.TryGetComp<CompRefuelable>() is { } fuel) fuel.Refuel(fuel.Props.fuelCapacity * 0.37f);
                if (t.def.category == ThingCategory.Building) t = t.MakeMinified();
                if (!GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near)) throw new CommandException("fixture_failed", name);
                result.Add(new { reference = bridge.Objects.Add(t), data = ThingState(t) });
            }
            return new { fixture = true, items = result };
        });
        bridge.Router.Register("clash.fixture.remoteLoss", "FIXTURE: destroy one steel stack on an active remote map, exercising ordinary destruction callbacks; no server or home map writes.", "fixture", a =>
        {
            Fixture(a); var session = Session();
            if (session?.IsActive != true) throw new CommandException("invalid_context", "Requires remote map.");
            var map = Find.Maps.Single(m => "Map_" + m.uniqueID == session.ClientMapId);
            var thing = map.listerThings.ThingsOfDef(ThingDefOf.Steel).OrderBy(t => t.ThingID, StringComparer.Ordinal).First();
            var before = new { id = thing.ThingID, count = thing.stackCount, defName = thing.def.defName };
            thing.Destroy(DestroyMode.Vanish);
            return new { fixture = true, before };
        });
    }
    private void Fixture(JsonElement a)
    {
        Loopback();
        if (!a.Bool("acknowledgeFixture") || !bridge.Router.Capabilities.Any(c => c.Method == "debug.invoke"))
            throw new CommandException("disabled", "Explicit fixture acknowledgement and AllowDebug required.");
    }
    private Pawn FindPawn(JsonElement a) => a.OptionalString("pawn") is { } handle ? bridge.Objects.Get<Pawn>(handle) :
        Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Single(p => p.ThingID == a.String("thingId"));
    private static Map StabilityMap(JsonElement a) => a.OptionalString("mapId") is { } id
        ? Find.Maps.Single(m => "Map_" + m.uniqueID == id) : Find.Maps.Single(m => m.IsPlayerHome);
    private static object PawnState(Pawn p) => new
    {
        name = p.Name?.ToStringFull, race = p.def.defName, kind = p.kindDef.defName, gender = p.gender.ToString(),
        childhood = p.story?.Childhood?.defName, adulthood = p.story?.Adulthood?.defName,
        traits = p.story?.traits.allTraits.Select(t => new { defName = t.def.defName, degree = t.Degree }).OrderBy(t => t.defName, StringComparer.Ordinal).ToArray(),
        skills = p.skills?.skills.OrderBy(s => s.def.defName, StringComparer.Ordinal).Select(s => new { defName = s.def.defName, level = s.Level, passion = s.passion.ToString() }).ToArray(),
        permanentHealth = p.health.hediffSet.hediffs.Where(h => h.IsPermanent()).Select(h => new { defName = h.def.defName, part = h.Part?.Index, severity = h.Severity }).OrderBy(h => h.defName, StringComparer.Ordinal).ThenBy(h => h.part).ToArray(),
        genes = p.genes?.GenesListForReading.OrderBy(g => g.def.defName, StringComparer.Ordinal).Select(g => new { defName = g.def.defName, xenogene = p.genes.Xenogenes.Contains(g), active = g.Active }).ToArray(),
        // Relationship stubs promise LabelShort, species and stable identity, not a full NameTriple.
        relations = p.relations?.DirectRelations.OrderBy(r => r.def.defName, StringComparer.Ordinal).ThenBy(r => r.otherPawn.LabelShort, StringComparer.Ordinal)
            .Select(r => new { defName = r.def.defName, name = r.otherPawn.LabelShort, race = r.otherPawn.def.defName, kind = r.otherPawn.kindDef.defName, dead = r.otherPawn.Dead }).ToArray(),
        equipment = p.equipment?.AllEquipmentListForReading.OrderBy(t => t.def.defName, StringComparer.Ordinal).Select(ThingState).ToArray(),
        apparel = p.apparel?.WornApparel.OrderBy(t => t.def.defName, StringComparer.Ordinal).Select(ThingState).ToArray()
    };
    private static object ThingState(Thing t)
    {
        var value = t is MinifiedThing m ? m.InnerThing : t;
        if (value is null) return new { defName = t.def.defName, emptyMinified = true };
        var coded = value.TryGetComp<CompBiocodable>();
        return new
        {
            defName = t.def.defName, innerDef = t is MinifiedThing ? value.def.defName : null, count = t.stackCount,
            stuff = value.Stuff?.defName, quality = value.TryGetComp<CompQuality>()?.Quality.ToString(),
            hp = value.def.useHitPoints ? (int?)value.HitPoints : null, tainted = (value as Apparel)?.WornByCorpse,
            deterioration = AmbientDamageObservation.Loss(value),
            biocoded = coded?.Biocoded, codedPawn = coded?.Biocoded == true ? coded.CodedPawnLabel : null,
            ingredients = value.TryGetComp<CompIngredients>()?.ingredients.Select(d => d.defName).Order(StringComparer.Ordinal).ToArray(),
            genes = (value as GeneSetHolderBase)?.GeneSet?.GenesListForReading.Select(g => g.defName).Order(StringComparer.Ordinal).ToArray(),
            color = value.TryGetComp<CompColorable>()?.Color.ToString(),
            style = value.StyleDef?.defName,
            rotTicks = value.TryGetComp<CompRottable>()?.RotProgress,
            fuel = value.TryGetComp<CompRefuelable>()?.Fuel
        };
    }
}
