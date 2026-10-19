using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

[HarmonyPatch(typeof(SettlementAbandonUtility), nameof(SettlementAbandonUtility.AbandonCommand))]
internal static class PlayerColonyAbandonCommandPatch
{
    public static void Postfix(MapParent settlement, ref Command __result)
    {
        if (settlement is not null && ClientColonyGizmos.Current?.Abandon(settlement, __result) is { } command)
            __result = command;
    }
}
