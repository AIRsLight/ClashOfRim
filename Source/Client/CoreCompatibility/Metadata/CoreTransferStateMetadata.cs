using System;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

/// <summary>Mutable vanilla component state shared by trade, gift and shop transfers.</summary>
internal static class CoreTransferStateMetadata
{
    private const string Prefix = "clashofrim.core.state.";

    public static void Append(Thing thing, ModThingReferenceDto reference)
    {
        if (thing.TryGetComp<CompRottable>() is { } rot) reference.Metadata[Prefix + "rot"] = Number(rot.RotProgress);
        if (thing.TryGetComp<CompRefuelable>() is { } fuel) reference.Metadata[Prefix + "fuel"] = Number(fuel.Fuel);
        if (thing.TryGetComp<CompIngredients>() is { } ingredients)
            reference.Metadata[Prefix + "ingredients"] = string.Join(",", ingredients.ingredients.Select(d => d.defName));
        if (thing.TryGetComp<CompColorable>() is { Active: true } color)
            reference.Metadata[Prefix + "color"] = string.Join(",", new[] { color.Color.r, color.Color.g, color.Color.b, color.Color.a }.Select(Number));
    }

    public static bool TryApply(ModThingReferenceDto reference, Thing thing, out string? failure)
    {
        failure = null;
        var data = reference.Metadata;
        if (data is null) return true;
        bool hasRot = data.TryGetValue(Prefix + "rot", out var rotText);
        bool hasFuel = data.TryGetValue(Prefix + "fuel", out var fuelText);
        var rot = thing.TryGetComp<CompRottable>();
        var fuel = thing.TryGetComp<CompRefuelable>();
        float rotValue = 0, fuelValue = 0;
        if (hasRot && (rot is null || !ReadNumber(rotText, out rotValue)))
        {
            failure = Prefix + "rot";
            return false;
        }
        if (hasFuel && (fuel is null || !ReadNumber(fuelText, out fuelValue)))
        {
            failure = Prefix + "fuel";
            return false;
        }

        var color = thing.TryGetComp<CompColorable>();
        float[]? components = null;
        if (data.TryGetValue(Prefix + "color", out var colorText))
        {
            var values = colorText?.Split(',');
            if (color is null || values?.Length != 4)
            {
                failure = Prefix + "color";
                return false;
            }
            components = new float[4];
            for (int i = 0; i < 4; i++)
            {
                if (!ReadNumber(values[i], out components[i]) || components[i] > 1)
                {
                    failure = Prefix + "color";
                    return false;
                }
            }
        }

        var ingredients = thing.TryGetComp<CompIngredients>();
        ThingDef[]? definitions = null;
        if (data.TryGetValue(Prefix + "ingredients", out var ingredientText))
        {
            if (ingredients is null || ingredientText is null)
            {
                failure = Prefix + "ingredients";
                return false;
            }
            string[] names = ingredientText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            definitions = new ThingDef[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(names[i]);
                if (def is null)
                {
                    failure = names[i];
                    return false;
                }
                definitions[i] = def;
            }
        }
        // Resolve and validate the entire state before changing any component.
        if (hasRot) rot!.RotProgress = rotValue;
        if (hasFuel)
        {
            fuel!.ConsumeFuel(fuel.Fuel);
            fuel.Refuel(Math.Min(fuelValue, fuel.Props.fuelCapacity));
        }
        if (components is not null) color!.SetColor(new Color(components[0], components[1], components[2], components[3]));
        if (definitions is not null)
        {
            ingredients!.ingredients.Clear();
            ingredients.ingredients.AddRange(definitions);
        }
        return true;
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool ReadNumber(string? value, out float number) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
        && !float.IsNaN(number) && !float.IsInfinity(number) && number >= 0;
}
