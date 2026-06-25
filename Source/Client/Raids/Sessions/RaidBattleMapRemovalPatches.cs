using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

[HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
public static class RaidBattleMapRemovalPatches
{
    public static bool Prefix(MapParent __instance)
    {
        return !ClashOfRimGameComponent.TryHandleRaidBattleMapRemovalCheck(__instance);
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
public static class RaidBattleDirectMapRemovalPatches
{
    public static bool Prefix(Map map)
    {
        return !ClashOfRimGameComponent.TryHandleRaidBattleDirectMapRemoval(map);
    }
}
