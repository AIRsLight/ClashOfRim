using HarmonyLib;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapSettled))]
internal static class PlayerColonySiteSettledPatch
{
    public static void Postfix(MapParent __instance, Map map)
    {
        if (IsPlayerColonyContext(__instance, map))
        {
            RequestRegistration(ClashOfRimText.Key("ClashOfRim.WorldCatalog.ReasonPlayerColonyMapCreated"));
        }
    }

    private static bool IsPlayerColonyContext(MapParent parent, Map map)
    {
        return map?.IsPlayerHome == true
            || parent?.Faction == Faction.OfPlayer;
    }

    private static void RequestRegistration(string reason)
    {
        try
        {
            ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
            if (mod.IsPlayerColonySiteRegistrationSuppressed
                || ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility())
            {
                ClashLog.Message("[ClashOfRim] Player colony site registration suppressed: " + reason);
                return;
            }

            mod.RequestPlayerColonySiteRegistration(reason);
        }
        catch (System.Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to request player colony site registration: " + ex);
        }
    }
}

[HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapRemoved))]
internal static class PlayerColonySiteRemovedPatch
{
    public static void Postfix(MapParent __instance, Map map)
    {
        if (map?.IsPlayerHome == true || __instance?.Faction == Faction.OfPlayer)
        {
            try
            {
                ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
                if (mod.IsPlayerColonySiteRegistrationSuppressed
                    || ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility())
                {
                    ClashLog.Message("[ClashOfRim] Player colony site removal registration suppressed.");
                    return;
                }

                mod.RequestPlayerColonySiteRegistration(ClashOfRimText.Key("ClashOfRim.WorldCatalog.ReasonPlayerColonyMapRemoved"));
            }
            catch (System.Exception ex)
            {
                Log.Warning("[ClashOfRim] Failed to request player colony site removal registration: " + ex);
            }
        }
    }
}
