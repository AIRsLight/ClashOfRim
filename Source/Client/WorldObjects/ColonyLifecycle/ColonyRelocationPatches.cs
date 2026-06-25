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
