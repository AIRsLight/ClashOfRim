using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(Page_ChooseIdeoPreset), "DoClassic")]
internal static class ClassicIdeoSetupPatch
{
    internal static IEnumerable<Faction> ClassicFactions(FactionManager manager)
    {
        if (!ClientSharedIdeologyWorld.IsSharedWorld) return manager.AllFactions;
        // The player's Ideo remains classic; the shared world also contains full
        // NPC/player ideologies whose load rules must not depend on this choice.
        Find.IdeoManager.classicMode = false;
        return manager.AllFactions.Where(faction => faction.IsPlayer);
    }

    // Vanilla Classic applies the player's choice to every faction. A shared
    // world's NPC ideologies are authoritative, even before the first map login.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var allFactions = AccessTools.PropertyGetter(typeof(FactionManager), nameof(FactionManager.AllFactions));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(allFactions))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ClassicIdeoSetupPatch), nameof(ClassicFactions));
            }
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(Page_SelectStartingSite), nameof(Page_SelectStartingSite.PostOpen))]
internal static class SharedIdeologySetupWorldPatch
{
    private static void Prefix()
    {
        ClientSharedIdeologyWorld.Current?.StartingSiteOpened();
    }
}
