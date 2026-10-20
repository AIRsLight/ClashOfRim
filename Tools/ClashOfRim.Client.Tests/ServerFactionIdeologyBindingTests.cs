using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.DlcCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ServerFactionIdeologyBindingTests
{
    private static FactionManager factions = null!;
    private static IdeoManager ideos = null!;
    private static List<Pawn> pawns = new();
    private static int nextId, loads;
    private static bool Factions(ref FactionManager __result) { __result = factions; return false; }
    private static bool Ideos(ref IdeoManager __result) { __result = ideos; return false; }
    private static bool Pawns(ref List<Pawn> __result) { __result = pawns; return false; }
    private static bool Enabled(ref bool __result) { __result = true; return false; }
    private static bool Load(ModWorldIdeoSummaryDto dto, ref Ideo? __result)
    { loads++; __result = new Ideo { id = nextId++, name = dto.Name }; return false; }
    private static bool Copy(Ideo __instance, Ideo __0) { __0.name = __instance.name; return false; }
    private static bool Sort() => false;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.ServerFactionIdeologyBindings");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.FactionManager)), prefix: Method(nameof(Factions)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.IdeoManager)), prefix: Method(nameof(Ideos)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PawnsFinder), nameof(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)), prefix: Method(nameof(Pawns)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(IdeologyPawnReferenceCompatibility), nameof(IdeologyPawnReferenceCompatibility.HasPawnReference)), prefix: Method(nameof(Enabled)));
            harmony.Patch(AccessTools.Method(typeof(RemoteIdeoMaterializer), "TryCreateIdeo"), prefix: Method(nameof(Load)));
            harmony.Patch(AccessTools.Method(typeof(Ideo), nameof(Ideo.CopyTo)), prefix: Method(nameof(Copy)));
            harmony.Patch(AccessTools.Method(typeof(IdeoManager), nameof(IdeoManager.SortIdeos)), prefix: Method(nameof(Sort)));
            foreach (bool reverse in new[] { false, true })
            {
                Reset();
                var shared = AddIdeo("local shared");
                var a = Faction("TribeCivil", shared); var b = Faction("TribeRoughNeanderthal", shared);
                var pawnA = Pawn(a, shared); var pawnB = Pawn(b, shared);
                var player = Faction("PlayerColony", AddIdeo("player")); player.def.isPlayer = true;
                var adopted = Pawn(player, shared);
                var left = Dto("left", "TribeCivil"); var right = Dto("right", "TribeRoughNeanderthal");
                var catalog = reverse ? new[] { right, left } : new[] { left, right };
                Apply(catalog);
                Check(a.ideos.PrimaryIdeo.name == "left" && b.ideos.PrimaryIdeo.name == "right"
                    && a.ideos.PrimaryIdeo != b.ideos.PrimaryIdeo, "Shared local instance overwrote a canonical ideology.");
                Check(pawnA.ideo.Ideo == a.ideos.PrimaryIdeo && pawnB.ideo.Ideo == b.ideos.PrimaryIdeo
                    && adopted.ideo.Ideo == shared && shared.name == "local shared", "Pawn references crossed faction ownership.");
                Check(pawnA.ideo.PreviousIdeos.Count == 0
                    && (float)AccessTools.Field(typeof(Pawn_IdeoTracker), "certaintyInt").GetValue(pawnA.ideo) == 0.73f,
                    "Reference repair acted as conversion.");
                int count = ideos.IdeosListForReading.Count, loaded = loads;
                Apply(catalog);
                Check(ideos.IdeosListForReading.Count == count && loads == loaded, "Repeated application created or reloaded ideologies.");
                // Reload clears process-local keys; live faction references remain sufficient.
                foreach (var i in ideos.IdeosListForReading) RemoteIdeoCatalog.Unregister(i);
                Apply(catalog);
                Check(loads == loaded && a.ideos.PrimaryIdeo == pawnA.ideo.Ideo, "Reload lost canonical references.");
            }

            Reset();
            var oldA = AddIdeo("a"); var oldB = AddIdeo("b"); var oldMinor = AddIdeo("minor");
            var first = Faction("Repeated", oldA); var second = Faction("Repeated", oldB);
            first.ideos.IdeosMinorListForReading.Add(oldMinor);
            var follower = Pawn(second, oldB); var minorFollower = Pawn(first, oldMinor);
            var merged = Dto("merged", "Repeated");
            merged.FactionBindings!.Add(new() { FactionDefName = "Repeated", FactionIndex = 1 });
            var minority = Dto("minority", "Repeated"); minority.FactionBindings![0].IdeoIndex = 1;
            Apply(new[] { minority, merged });
            Check(first.ideos.PrimaryIdeo == second.ideos.PrimaryIdeo && follower.ideo.Ideo == first.ideos.PrimaryIdeo,
                "Authoritative sharing was not restored for repeated faction Defs.");
            Check(first.ideos.IdeosMinorListForReading.Single() == minorFollower.ideo.Ideo
                && minorFollower.ideo.Ideo.name == "minority", "Minor ideology slot was lost.");
            Check(RemoteIdeoMaterializer.TryGetOrCreate(merged, out _)
                && first.ideos.IdeosMinorListForReading.Single() == minorFollower.ideo.Ideo,
                "Resolving one catalog entry erased another entry's minor slot.");
            var captured = ServerFactionIdeologyBindings.Capture(first.ideos.PrimaryIdeo);
            Check(captured.Count == 2 && captured.Select(b => b.FactionIndex).SequenceEqual(new[] { 0, 1 })
                && captured.All(b => b.IdeoIndex == 0), "Capture collapsed shared/repeated faction bindings.");
            var legacy = Dto("legacy", "Repeated"); legacy.FactionBindings = null;
            bool refused = false;
            try { Apply(new[] { legacy }); } catch (InvalidOperationException) { refused = true; }
            Check(refused && first.ideos.PrimaryIdeo.name == "merged", "Ambiguous legacy faction was silently assigned.");
            var empty = Dto("orphan", "Repeated"); empty.FactionBindings = new();
            Check(!new ServerFactionIdeologyBindings(new[] { empty }).HasBindings(empty), "Explicit empty bindings used the legacy fallback.");
        }
        finally
        {
            if (ideos is not null) foreach (var i in ideos.IdeosListForReading) RemoteIdeoCatalog.Unregister(i);
            harmony.UnpatchAll(harmony.Id);
        }
    }
    private static void Reset()
    {
        if (ideos is not null) foreach (var i in ideos.IdeosListForReading) RemoteIdeoCatalog.Unregister(i);
        factions = new(); ideos = new(); pawns = new(); nextId = 100; loads = 0;
    }
    private static Ideo AddIdeo(string name)
    { var ideo = new Ideo { id = nextId++, name = name }; ideos.IdeosListForReading.Add(ideo); return ideo; }
    private static Faction Faction(string def, Ideo ideo)
    {
        var faction = new Faction { def = new FactionDef { defName = def }, loadID = factions.AllFactionsListForReading.Count };
        faction.ideos = new(faction); faction.ideos.SetPrimary(ideo); factions.AllFactionsListForReading.Add(faction); return faction;
    }
    private static Pawn Pawn(Faction faction, Ideo ideo)
    {
        var pawn = new Pawn();
        AccessTools.Field(typeof(Thing), "factionInt").SetValue(pawn, faction);
        pawn.ideo = new(pawn);
        AccessTools.Field(typeof(Pawn_IdeoTracker), "ideo").SetValue(pawn.ideo, ideo);
        AccessTools.Field(typeof(Pawn_IdeoTracker), "certaintyInt").SetValue(pawn.ideo, 0.73f);
        pawns.Add(pawn); return pawn;
    }
    private static ModWorldIdeoSummaryDto Dto(string name, string faction) => new()
    {
        GlobalKey = "owner:server/ideo:" + name, OwnerUserId = "server", Name = name, FactionDefName = faction,
        FactionBindings = new() { new() { FactionDefName = faction } }
    };
    private static void Apply(ModWorldIdeoSummaryDto[] catalog) =>
        Check(RemoteIdeoMaterializer.ApplyServerCatalog(catalog, "player") == catalog.Length, "Catalog application failed.");
    private static HarmonyMethod Method(string name) => new(typeof(ServerFactionIdeologyBindingTests), name);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
