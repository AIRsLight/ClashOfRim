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
        if (settlement?.Faction != Faction.OfPlayer)
        {
            return;
        }

        ClashOfRimMod mod;
        try
        {
            mod = LoadedModManager.GetMod<ClashOfRimMod>();
        }
        catch
        {
            return;
        }

        if (!mod.IsNetworkConfigured)
        {
            return;
        }

        Command? original = __result;
        var replacement = new Command_Action
        {
            defaultLabel = original?.Label ?? "CommandAbandonHome".Translate().ToString(),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.AbandonColony.CommandDesc"),
            icon = original is Command originalCommand ? originalCommand.icon : null,
            Order = original?.Order ?? GizmoOrder.AbandonSettlement,
            action = () => mod.OpenAbandonPlayerColonyConfirmation(settlement)
        };
        __result = replacement;
    }
}
