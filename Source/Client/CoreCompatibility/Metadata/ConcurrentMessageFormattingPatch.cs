using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

// Network continuations and the game UI share Verse's static argument/buffer
// caches. Synchronize both entry points, including vanilla callers; locking only
// ClashOfRimText would still race with the UI. Monitor supports nested formatting.
[HarmonyPatch]
internal static class ConcurrentMessageFormattingPatch
{
    private static readonly object Gate = new();

    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(GrammarResolverSimpleStringExtensions))
            .Where(method => method.Name == "Formatted")
            .Cast<MethodBase>()
            .Append(AccessTools.Method(typeof(GrammarResolverSimple), "Formatted"));

    private static void Prefix(out bool __state)
    {
        __state = false;
        Monitor.Enter(Gate, ref __state);
    }

    private static void Finalizer(bool __state)
    {
        if (__state) Monitor.Exit(Gate);
    }
}
