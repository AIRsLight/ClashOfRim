using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[]
{
    typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool)
})]
internal static class RemoteMapLoadedItemSpawnPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var capacity = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetMaxItemsAllowedInCell));
        var matches = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.Calls(capacity)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException("Remote item restoration requires the native GenSpawn cell-capacity check.");

        var (call, offset) = matches[0];
        // Only the overflow check inside Spawn receives the load context. All other
        // capacity queries (hauling, building removal, etc.) keep their native limits.
        code.Insert(offset, new CodeInstruction(OpCodes.Ldarg_S, (byte)5).MoveLabelsFrom(call));
        call.operand = AccessTools.Method(typeof(RemoteMapLoadedItemSpawnPatch), nameof(CapacityDuringSpawn));
        return code;
    }

    private static int CapacityDuringSpawn(IntVec3 cell, Map map, bool respawningAfterLoad)
    {
        // Native save loading restores items before buildings and skips overflow in
        // MapInitializing. Remote loading happens while Playing: preserve the same
        // saved stacks without changing global program state or caching shelf cells.
        if (respawningAfterLoad && RemoteMapProjectionLoadScope.Active
            && RemoteSessionGlobalStateGuard.IsRemoteMap(map))
            return int.MaxValue;

        return cell.GetMaxItemsAllowedInCell(map);
    }
}
