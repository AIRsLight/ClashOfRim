using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

// Scribe reads numeric values invariantly, but writes them using CurrentCulture.
// Cover full snapshots, standalone difficulty/pawn/world XML and grammar weights
// stored in Def data. UI culture and the caller's number formatting stay intact.
[HarmonyPatch]
internal static class InvariantGameDataCulturePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Game), nameof(Game.ExposeData));
        yield return AccessTools.Method(typeof(ScribeSaver), nameof(ScribeSaver.DebugOutputFor));
        yield return AccessTools.Constructor(typeof(Verse.Grammar.Rule_String), new[] { typeof(string) });
    }

    private static void Prefix(out CultureInfo __state)
    {
        __state = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static void Finalizer(CultureInfo __state)
    {
        CultureInfo.CurrentCulture = __state;
    }
}
