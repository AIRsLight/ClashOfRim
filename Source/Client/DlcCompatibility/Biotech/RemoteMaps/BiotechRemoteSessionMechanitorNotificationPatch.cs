using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechRemoteSessionMechanitorNotificationPatch
{
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.Notify_PawnGotoLeftMap));
        if (original is null)
        {
            Log.Warning("[ClashOfRim][Biotech] Mechanitor left-map notification patch target was not found.");
            return;
        }

        var prefix = new HarmonyMethod(
            typeof(BiotechRemoteSessionMechanitorNotificationPatch),
            nameof(Prefix));
        harmony.Patch(original, prefix: prefix);
    }

    public static bool Prefix(Pawn pawn, Map map)
    {
        if (map?.Parent is not RemoteSessionMapParent)
        {
            return true;
        }

        if (RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects)
        {
            return false;
        }

        return !RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn);
    }
}
