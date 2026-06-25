using AIRsLight.ClashOfRim;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(IdeoDevelopmentUtility), nameof(IdeoDevelopmentUtility.ApplyChangesToIdeo))]
internal static class IdeoReformSyncPatches
{
    public static void Postfix(Ideo ideo)
    {
        if (!IsLocalPlayerIdeo(ideo))
        {
            return;
        }

        try
        {
            LoadedModManager.GetMod<ClashOfRimMod>()
                .RequestWorldConfigurationExtensionSync(ClashOfRimText.Key("ClashOfRim.WorldCatalog.ReasonIdeoReformed"));
        }
        catch (System.Exception ex)
        {
            Log.Warning("[ClashOfRim][Ideo] Failed to request world extension sync after reform: " + ex);
        }
    }

    private static bool IsLocalPlayerIdeo(Ideo ideo)
    {
        if (ideo is null
            || !IdeologyPawnReferenceCompatibility.HasPawnReference
            || RemoteIdeoCatalog.IsRemoteShadow(ideo))
        {
            return false;
        }

        return ideo.initialPlayerIdeo
            || Faction.OfPlayer?.ideos?.Has(ideo) == true;
    }
}
