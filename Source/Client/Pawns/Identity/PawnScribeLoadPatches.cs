using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

[HarmonyPatch(typeof(LifeStageWorker_HumanlikeAdult), nameof(LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted))]
public static class PawnScribeLoadLifeStagePatch
{
    public static bool Prefix(LifeStageDef previousLifeStage)
    {
        return Scribe.mode == LoadSaveMode.Inactive || previousLifeStage is not null;
    }
}
