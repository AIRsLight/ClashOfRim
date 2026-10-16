using System;
using System.Collections.Generic;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ThingMetadataOrderTests
{
    private static bool Maximum(Thing __instance, ref int __result)
    {
        __result = __instance.TryGetComp<CompQuality>()?.Quality == QualityCategory.Masterwork ? 200 : 100;
        return false;
    }
    public static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.ThingMetadataOrder");
        var method = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.Trades.TradeThingReferenceUtility", true)!
            .GetMethod("ApplyThingMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
        var thing = new ThingWithComps { def = new ThingDef { defName = "MetadataTest", useHitPoints = true } };
        var quality = new CompQuality { parent = thing };
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(thing, new List<ThingComp> { quality });
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MaxHitPoints)), prefix: new HarmonyMethod(typeof(ThingMetadataOrderTests), nameof(Maximum)));
            method.Invoke(null, new object?[] { thing, new ModThingReferenceDto(), "Masterwork", 150, null });
            if (thing.HitPoints != 150) throw new Exception("Durability was clamped before final quality.");
            method.Invoke(null, new object?[] { thing, new ModThingReferenceDto(), "Normal", 150, null });
            if (thing.HitPoints != 100) throw new Exception("Durability exceeded final-quality maximum.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
