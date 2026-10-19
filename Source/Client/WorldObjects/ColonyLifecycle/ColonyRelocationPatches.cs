using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.SettleCommand))]
internal static class ColonyRelocationSettleCommandPatch
{
    public static void Postfix(Caravan caravan, ref Command __result)
    {
        if (caravan is not null && __result is not null
            && ClientColonyRelocationCommands.Current?.Build(caravan, __result) is { } replacement)
            __result = replacement;
    }
}

[HarmonyPatch(typeof(SettleInExistingMapUtility), nameof(SettleInExistingMapUtility.SettleCommand))]
internal static class ColonyRelocationExistingMapSettleCommandPatch
{
    public static void Postfix(Map map, bool requiresNoEnemies, ref Command __result)
    {
        if (map is not null && __result is not null
            && ClientColonyRelocationCommands.Current?.Build(map, __result, requiresNoEnemies) is { } replacement)
            __result = replacement;
    }
}
