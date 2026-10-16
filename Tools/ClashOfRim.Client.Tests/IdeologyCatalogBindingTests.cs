using System;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.DlcCompatibility;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class IdeologyCatalogBindingTests
{
    private static IdeoManager ideos = null!;
    private static FactionManager factions = null!;
    private static World world = null!;
    private static bool ReadIdeos(ref IdeoManager __result) { __result = ideos; return false; }
    private static bool ReadFactions(ref FactionManager __result) { __result = factions; return false; }
    private static bool ReadWorld(ref World __result) { __result = world; return false; }

    public static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.IdeologyBinding");
        ideos = new IdeoManager();
        factions = new FactionManager();
        var stale = new Ideo { id = 1, name = "Stale", initialPlayerIdeo = true };
        var live = new Ideo { id = 2, name = "Live" };
        ideos.IdeosListForReading.Add(stale);
        ideos.IdeosListForReading.Add(live);
        var faction = new Faction { def = new FactionDef { defName = "OutlanderCivil" } };
        faction.ideos = new FactionIdeosTracker(faction);
        faction.ideos.SetPrimary(live);
        factions.AllFactionsListForReading.Add(faction);
        var dto = new ModWorldIdeoSummaryDto { GlobalKey = "owner:server/ideo:7", OwnerUserId = "server", FactionDefName = "OutlanderCivil", Name = "Live" };
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.IdeoManager)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadIdeos)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.FactionManager)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadFactions)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)), prefix: new HarmonyMethod(typeof(IdeologyCatalogBindingTests), nameof(ReadWorld)));
            RemoteIdeoCatalog.Register(stale, dto.GlobalKey);
            if (!RemoteIdeoMaterializer.TryGetOrCreate(dto, out var resolved) || !ReferenceEquals(resolved, live))
                throw new Exception("A superseded catalog entry won over the faction's current primary ideology.");
            if (!RemoteIdeoCatalog.TryGetCatalogIdeo(dto.GlobalKey, out resolved) || !ReferenceEquals(resolved, live))
                throw new Exception("Catalog was not rebound to the live faction instance.");

            var player = new Faction { def = new FactionDef { defName = "PlayerColony", isPlayer = true } };
            factions.AllFactionsListForReading.Add(player);
            ideos.classicMode = true;
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(new[] { player }))
                throw new Exception("Classic setup included authoritative NPC factions before map login.");
            if (ideos.classicMode)
                throw new Exception("The player's Classic choice disabled full ideologies for the shared world.");

            dto.GlobalKey = "owner:alice/ideo:7"; dto.OwnerUserId = "alice";
            RemoteIdeoCatalog.Register(stale, dto.GlobalKey);
            if (!RemoteIdeoMaterializer.TryGetOrCreate(dto, out resolved) || !ReferenceEquals(resolved, stale))
                throw new Exception("A player-owned ideology was replaced by a native faction ideology.");

            RemoteIdeoCatalog.Unregister(live);
            RemoteIdeoCatalog.Unregister(stale);
            ideos.classicMode = true;
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(factions.AllFactions))
                throw new Exception("Single-player Classic setup no longer includes every faction.");
            if (!ideos.classicMode)
                throw new Exception("Single-player Classic mode was disabled.");

            world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            ClassicIdeoSetupPatch.RememberSharedWorld(world);
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(new[] { player }))
                throw new Exception("Host setup lost scope after publishing and consuming its pending flag.");
            world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            if (!ClassicIdeoSetupPatch.ClassicFactions(factions).SequenceEqual(factions.AllFactions))
                throw new Exception("Shared setup scope leaked into a different single-player world.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            RemoteIdeoCatalog.Unregister(stale); RemoteIdeoCatalog.Unregister(live);
        }
    }
}
