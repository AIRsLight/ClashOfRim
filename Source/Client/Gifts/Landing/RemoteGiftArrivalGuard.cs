using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

// Arrived sets the engine's irreversible arrival flag before calling the action. Defer before that
// flag, so another upload cannot save cargo removal without its gift transaction.
[HarmonyPatch(typeof(TravellingTransporters), "Arrived")]
internal static class RemoteGiftArrivalGuard
{
    private static bool Prefix(TransportersArrivalAction ___arrivalAction)
    {
        if (___arrivalAction is not RemoteGiftTransportersArrivalAction) return true;
        var mod = LoadedModManager.GetMod<ClashOfRimMod>();
        return mod is not null && mod.CanStartTransportPodGiftArrival();
    }
}
