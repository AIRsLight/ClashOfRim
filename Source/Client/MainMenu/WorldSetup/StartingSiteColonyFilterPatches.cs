using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(WorldRenderer), nameof(WorldRenderer.DrawWorldLayers))]
internal static class StartingSiteFilterRefreshPatch
{
    private static void Prefix() => ClientWorldSetupPages.Current?.SiteFilter?.Refresh();
}

[HarmonyPatch]
internal static class StartingSiteFilterStaticIconsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(WorldDrawLayer_WorldObjects_Expandable), "ShouldSkip");
        yield return AccessTools.Method(typeof(WorldDrawLayer_WorldObjects_NonExpandable), "ShouldSkip");
    }
    private static void Postfix(WorldObject worldObject, ref bool __result)
        => __result |= StartingSiteColonyFilter.Hides(worldObject);
}

[HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.ExpandableWorldObjectsOnGUI))]
internal static class StartingSiteFilterExpandedIconsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => StartingSiteFilterDrawCalls.Replace(instructions,
            AccessTools.Method(typeof(List<WorldObject>), nameof(List<WorldObject>.AddRange)),
            AccessTools.Method(typeof(StartingSiteFilterExpandedIconsPatch), nameof(AddVisible)));

    internal static void AddVisible(List<WorldObject> display, IEnumerable<WorldObject> source)
    {
        ClientWorldSetupPages.Current?.SiteFilter?.Refresh();
        if (!StartingSiteColonyFilter.Active) { display.AddRange(source); return; }
        foreach (var value in source)
            if (!StartingSiteColonyFilter.Hides(value)) display.Add(value);
    }
}

[HarmonyPatch(typeof(WorldDynamicDrawManager), nameof(WorldDynamicDrawManager.DrawDynamicWorldObjects))]
internal static class StartingSiteFilterDynamicIconsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => StartingSiteFilterDrawCalls.Replace(instructions, AccessTools.Method(typeof(WorldObject), nameof(WorldObject.Draw)),
            AccessTools.Method(typeof(StartingSiteFilterDynamicIconsPatch), nameof(DrawVisible)));
    internal static void DrawVisible(WorldObject value)
    { if (!StartingSiteColonyFilter.Hides(value)) value.Draw(); }
}

[HarmonyPatch(typeof(GenWorldUI), nameof(GenWorldUI.WorldObjectsUnderMouse))]
internal static class StartingSiteFilterMousePatch
{
    private static void Postfix(List<WorldObject> __result)
    {
        if (!StartingSiteColonyFilter.Active) return;
        for (int i = __result.Count - 1; i >= 0; i--)
            if (StartingSiteColonyFilter.Hides(__result[i])) __result.RemoveAt(i);
    }
}

internal static class StartingSiteFilterDrawCalls
{
    internal static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodInfo original, MethodInfo replacement)
    {
        var code = instructions.ToList();
        var calls = code.Where(instruction => instruction.Calls(original)).ToList();
        if (calls.Count != 1) throw new InvalidOperationException("Starting-site display filter requires the native draw call: " + original.Name);
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = replacement;
        return code;
    }
}
