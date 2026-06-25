using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal static class CompArtFixedText
{
    private const string SaveNodeName = "clashOfRimFixedArtDescription";

    private static readonly ConditionalWeakTable<CompArt, State> States = new();

    public static string? Get(CompArt? comp)
    {
        return comp is not null && States.TryGetValue(comp, out State state)
            ? state.FixedDescription
            : null;
    }

    public static void Set(CompArt? comp, string? fixedDescription)
    {
        if (comp is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(fixedDescription))
        {
            States.Remove(comp);
            return;
        }

        string normalized = fixedDescription!.Trim();
        States.GetOrCreateValue(comp).FixedDescription = normalized;
    }

    public static void Expose(CompArt comp)
    {
        string? fixedDescription = Get(comp);
        Scribe_Values.Look(ref fixedDescription, SaveNodeName, null);
        Set(comp, fixedDescription);
    }

    public static bool TryUseFixedText(CompArt comp, ref TaggedString result)
    {
        string? fixedDescription = Get(comp);
        if (string.IsNullOrWhiteSpace(fixedDescription))
        {
            return true;
        }

        result = fixedDescription!;
        return false;
    }

    private sealed class State
    {
        public string? FixedDescription;
    }
}

[HarmonyPatch(typeof(CompArt), nameof(CompArt.PostExposeData))]
internal static class CompArtFixedTextExposePatch
{
    private static void Postfix(CompArt __instance)
    {
        CompArtFixedText.Expose(__instance);
    }
}

[HarmonyPatch(typeof(CompArt), nameof(CompArt.GenerateImageDescription))]
internal static class CompArtFixedTextDescriptionPatch
{
    private static bool Prefix(CompArt __instance, ref TaggedString __result)
    {
        return CompArtFixedText.TryUseFixedText(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(CompStatue), nameof(CompStatue.GenerateImageDescription))]
internal static class CompStatueFixedTextDescriptionPatch
{
    private static bool Prefix(CompStatue __instance, ref TaggedString __result)
    {
        return CompArtFixedText.TryUseFixedText(__instance, ref __result);
    }
}
