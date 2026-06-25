using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class OdysseyGravshipLandingSnapshotPatch
{
    private static int gravshipLandingDepth;

    internal static bool SuppressPlayerColonySiteRegistration => gravshipLandingDepth > 0;

    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Scenario), nameof(Scenario.PostGravshipLanded));
        if (original is null)
        {
            Log.Warning("[ClashOfRim][Odyssey] Gravship landing snapshot patch target was not found.");
            return;
        }

        var postfix = new HarmonyMethod(
            typeof(OdysseyGravshipLandingSnapshotPatch),
            nameof(Postfix));
        var prefix = new HarmonyMethod(
            typeof(OdysseyGravshipLandingSnapshotPatch),
            nameof(Prefix));
        var finalizer = new HarmonyMethod(
            typeof(OdysseyGravshipLandingSnapshotPatch),
            nameof(Finalizer));
        harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
    }

    public static void Prefix()
    {
        gravshipLandingDepth++;
    }

    public static System.Exception? Finalizer(System.Exception? __exception)
    {
        if (gravshipLandingDepth > 0)
        {
            gravshipLandingDepth--;
        }

        return __exception;
    }

    public static void Postfix(Map map)
    {
        if (map is null || (map.IsPlayerHome != true && map.ParentFaction != Faction.OfPlayer))
        {
            return;
        }

        try
        {
            ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
            string reason = ClashOfRimText.Key("ClashOfRim.WorldCatalog.ReasonPlayerColonyMapCreated");
            ClashLog.Message("[ClashOfRim] Gravship landed on player map; starting implicit colony relocation confirmation.");
            mod.StartImplicitColonyRelocationConfirmation(map, reason);
        }
        catch (System.Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to start gravship landing snapshot upload: " + ex);
        }
    }
}
