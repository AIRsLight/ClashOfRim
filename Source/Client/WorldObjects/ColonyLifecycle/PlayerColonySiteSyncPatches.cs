using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapSettled))]
internal static class PlayerColonySiteSettledPatch
{
    public static void Postfix(MapParent __instance, Map map)
        => _ = ClientColonySiteEvents.Current?.NotifyAsync(__instance, map, ClientColonySiteEvent.Settled);
}

[HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapRemoved))]
internal static class PlayerColonySiteRemovedPatch
{
    public static void Postfix(MapParent __instance, Map map)
        => _ = ClientColonySiteEvents.Current?.NotifyAsync(__instance, map, ClientColonySiteEvent.Removed);
}
