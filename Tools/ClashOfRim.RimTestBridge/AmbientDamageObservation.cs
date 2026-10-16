using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

/// <summary>Observe actual vanilla deterioration during delivery ticks. Never suppress damage.</summary>
internal static class AmbientDamageObservation
{
    private sealed class Counter { public int Loss; }
    private static readonly ConditionalWeakTable<Thing, Counter> losses = new();
    public static int Loss(Thing thing) => losses.TryGetValue(thing, out var counter) ? counter.Loss : 0;
    public static void Install() => new Harmony("ClashOfRim.RimTest.AmbientDamage").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)),
        prefix: new HarmonyMethod(typeof(AmbientDamageObservation), nameof(Before)), postfix: new HarmonyMethod(typeof(AmbientDamageObservation), nameof(After)));
    private static void Before(Thing __instance, DamageInfo dinfo, out int __state) => __state = dinfo.Def == DamageDefOf.Deterioration ? __instance.HitPoints : -1;
    private static void After(Thing __instance, int __state)
    {
        if (__state >= 0) losses.GetOrCreateValue(__instance).Loss += Math.Max(0, __state - __instance.HitPoints);
    }
}
