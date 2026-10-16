using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

// 1.7's compatibility TextMesh has no rendering backend. Keep the game's TMP
// path (including the engine font backend) for localized names as well.
[HarmonyPatch(typeof(WorldFeatures), "CreateOrDestroyTexts")]
internal static class WorldFeatureTextCompatibilityPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var legacy = AccessTools.Constructor(typeof(WorldFeatureTextMesh_Legacy));
        var supported = AccessTools.Constructor(typeof(WorldFeatureTextMesh_TextMeshPro));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, legacy))
                instruction.operand = supported;
            yield return instruction;
        }
    }
}
