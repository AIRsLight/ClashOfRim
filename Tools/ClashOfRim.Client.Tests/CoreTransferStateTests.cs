using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static class CoreTransferStateTests
{
    public static void Run()
    {
        var utility = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.CoreCompatibility.CoreTransferStateMetadata", true)!;
        var append = utility.GetMethod("Append")!; var apply = utility.GetMethod("TryApply")!;
        var source = Item(); var target = Item(); var reference = new ModThingReferenceDto();
        source.TryGetComp<CompRottable>().RotProgress = 12000.25f;
        source.TryGetComp<CompRefuelable>().Refuel(18.5f);
        source.TryGetComp<CompColorable>().SetColor(new Color(.2f, .4f, .6f, .8f));
        var ingredient = new ThingDef { defName = "TransferStateIngredient" };
        DefDatabase<ThingDef>.Add(ingredient);
        source.TryGetComp<CompIngredients>().RegisterIngredient(ingredient);
        bool Apply(ModThingReferenceDto r) => (bool)apply.Invoke(null, new object?[] { r, target, null })!;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); append.Invoke(null, new object[] { source, reference });
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            if (!Apply(reference) || !Apply(reference) || target.TryGetComp<CompRottable>().RotProgress != 12000.25f
                || target.TryGetComp<CompRefuelable>().Fuel != 18.5f || target.TryGetComp<CompColorable>().Color != source.TryGetComp<CompColorable>().Color
                || !target.TryGetComp<CompIngredients>().ingredients.SequenceEqual(new[] { ingredient }))
                throw new Exception("Cross-culture state restore lost values or was not idempotent.");
            foreach (string invalid in new[] { "NaN", "Infinity", "-1", "12,5" })
            {
                var bad = new ModThingReferenceDto(); bad.Metadata["clashofrim.core.state.rot"] = "99"; bad.Metadata["clashofrim.core.state.fuel"] = invalid;
                if (Apply(bad) || target.TryGetComp<CompRottable>().RotProgress != 12000.25f) throw new Exception("Invalid component state partially mutated the item.");
            }
            var missingIngredient = new ModThingReferenceDto();
            missingIngredient.Metadata["clashofrim.core.state.rot"] = "99";
            missingIngredient.Metadata["clashofrim.core.state.ingredients"] = "MissingTransferIngredient";
            if (Apply(missingIngredient) || target.TryGetComp<CompRottable>().RotProgress != 12000.25f)
                throw new Exception("Unknown ingredient silently dropped state or partially applied it.");
            if (!Apply(new ModThingReferenceDto()) || target.TryGetComp<CompRefuelable>().Fuel != 18.5f) throw new Exception("Legacy payload reset component state.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    private static ThingWithComps Item()
    {
        var thing = new ThingWithComps { def = new ThingDef { defName = "TransferStateTest" } };
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(thing, new List<ThingComp>
        {
            new CompRottable { parent = thing, props = new CompProperties_Rottable() },
            new CompRefuelable { parent = thing, props = new CompProperties_Refuelable { fuelCapacity = 50 } },
            new CompColorable { parent = thing, props = new CompProperties(typeof(CompColorable)) },
            new CompIngredients { parent = thing, props = new CompProperties(typeof(CompIngredients)) }
        });
        return thing;
    }
}
