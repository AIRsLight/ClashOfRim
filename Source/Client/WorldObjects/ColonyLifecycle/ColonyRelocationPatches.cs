using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.SettleCommand))]
internal static class ColonyRelocationSettleCommandPatch
{
    public static void Postfix(Caravan caravan, ref Command __result)
    {
        if (caravan is null || __result is null)
        {
            return;
        }

        ClashOfRimMod mod;
        try
        {
            mod = LoadedModManager.GetMod<ClashOfRimMod>();
        }
        catch
        {
            return;
        }

        Command? replacement = mod.TryBuildColonyRelocationCommand(caravan, __result);
        if (replacement is not null)
        {
            __result = replacement;
        }
    }
}

[HarmonyPatch(typeof(SettleInExistingMapUtility), nameof(SettleInExistingMapUtility.SettleCommand))]
internal static class ColonyRelocationExistingMapSettleCommandPatch
{
    public static void Postfix(Map map, bool requiresNoEnemies, ref Command __result)
    {
        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        Command? replacement = mod.TryBuildColonyRelocationCommand(map, __result, requiresNoEnemies);
        if (replacement is not null)
        {
            __result = replacement;
        }
    }
}
