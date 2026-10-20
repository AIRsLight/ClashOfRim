using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class RemoteObservationDiplomacyTests
{
    private static Faction player = null!, owner = null!;
    private static int relationWrites, concealments, fogPasses;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RemoteObservationDiplomacy");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)), prefix: Method(nameof(Player)));
            harmony.Patch(AccessTools.Method(typeof(PlayerFactionProxyUtility), nameof(PlayerFactionProxyUtility.EnsureProxyForUser)), prefix: Method(nameof(Owner)));
            harmony.Patch(AccessTools.Method(typeof(Faction), nameof(Faction.SetRelationDirect)), prefix: Method(nameof(WriteRelation)));
            harmony.Patch(AccessTools.Method(typeof(MapPawns), nameof(MapPawns.PawnsInFaction)), prefix: Method(nameof(NoResidents)));
            // Isolate unrelated map sanitation/engine effects; execute the actual
            // postprocessor and its resident branch, including any diplomacy writes.
            Skip(typeof(RemoteSessionMapAreaSanitizer), "Apply");
            Skip(typeof(RemoteSessionMapConstructionSanitizer), "Apply");
            Skip(typeof(RemoteSessionPawnStateSanitizer), "DropLoadedCarriedThings");
            Skip(typeof(RemoteSessionMapThingAccessSanitizer), "ForbidLoadedHaulables");
            Skip(typeof(RaidDefenderMapUtility), "ConvertPlayerOwnedMapObjectsToDefenderProxy");
            Skip(typeof(ClashLog), "Message");
            harmony.Patch(AccessTools.Method(typeof(RemoteObservationMapPostProcessor), "ApplyHiddenTacticalObjectSession"), prefix: Method(nameof(Conceal)));
            harmony.Patch(AccessTools.Method(typeof(RemoteObservationMapPostProcessor), "ApplyEdgeReachableFog"), prefix: Method(nameof(Fog)));

            foreach (var relation in new[] { FactionRelationKind.Neutral, FactionRelationKind.Ally, FactionRelationKind.Hostile })
            foreach (string mode in new[] { RemoteSessionMapParent.ScoutMode, RemoteSessionMapParent.FriendlyObservationMode, RemoteSessionMapParent.RaidObservationMode })
            {
                player = new Faction { def = new FactionDef { defName = "Player", isPlayer = true }, loadID = 1 };
                owner = new Faction { def = new FactionDef { defName = "Owner" }, loadID = 2 };
                player.SetRelation(new FactionRelation(owner, relation));
                owner.SetRelation(new FactionRelation(player, relation));
                var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
                map.mapPawns = new MapPawns(map);
                var carrier = new RemoteSessionMapParent { OwnerUserId = "owner" };
                relationWrites = concealments = fogPasses = 0;
                RemoteObservationMapPostProcessor.Apply(map, carrier, mode, new ModSnapshotPackageMetadataDto());
                Check(relationWrites == 0 && owner.RelationKindWith(player) == relation && player.RelationKindWith(owner) == relation,
                    $"Observing {relation} owner in {mode} changed global diplomacy.");
                bool concealed = mode != RemoteSessionMapParent.FriendlyObservationMode;
                Check(concealments == (concealed ? 1 : 0) && fogPasses == concealments,
                    "Preserving diplomacy changed observation trap/fog policy.");
            }

            void Skip(Type type, string name) => harmony.Patch(AccessTools.Method(type, name), prefix: Method(nameof(NoEffect)));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool NoResidents(ref List<Pawn> __result) { __result = new(); return false; }
    private static bool Owner(ref Faction __result) { __result = owner; return false; }
    private static bool WriteRelation(Faction __instance, Faction __0, FactionRelationKind __1)
    {
        relationWrites++;
        __instance.SetRelation(new FactionRelation(__0, __1));
        return false;
    }
    private static bool Conceal() { concealments++; return false; }
    private static bool Fog() { fogPasses++; return false; }
    private static bool NoEffect() => false;
    private static HarmonyMethod Method(string name) => new(typeof(RemoteObservationDiplomacyTests), name);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
