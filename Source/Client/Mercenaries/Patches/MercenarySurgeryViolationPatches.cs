using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

[HarmonyPatch(typeof(Bill_Medical), nameof(Bill_Medical.Notify_IterationCompleted))]
public static class MercenarySurgeryViolationPatch
{
    public static void Postfix(Bill_Medical __instance, Pawn billDoer)
    {
        Pawn? patient = TryGetPatient(__instance);
        if (patient is null || patient.Dead || billDoer?.Faction != Faction.OfPlayer)
        {
            return;
        }

        QuestPart_ClashMercenary? part = ClashMercenaryQuestUtility.FindActivePartForPawn(patient);
        if (part is null || !IsHarmfulSurgery(__instance.recipe, __instance.Part, patient))
        {
            return;
        }

        part.ReportHostileBehavior();
    }

    private static Pawn? TryGetPatient(Bill_Medical bill)
    {
        try
        {
            return bill.GiverPawn;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Could not resolve medical bill patient for mercenary surgery check: " + ex.Message);
            return null;
        }
    }

    private static bool IsHarmfulSurgery(RecipeDef? recipe, BodyPartRecord? part, Pawn patient)
    {
        if (recipe?.Worker is null)
        {
            return false;
        }

        if (recipe.isViolation)
        {
            return true;
        }

        if (recipe.Worker is Recipe_RemoveBodyPart && part is not null)
        {
            return HealthUtility.PartRemovalIntent(patient, part) == BodyPartRemovalIntent.Harvest;
        }

        if (recipe.Worker is Recipe_InstallArtificialBodyPart && part is not null)
        {
            return IsHarvestingReplacedPart(recipe, patient, part);
        }

        return false;
    }

    private static bool IsHarvestingReplacedPart(RecipeDef recipe, Pawn patient, BodyPartRecord part)
    {
        return (recipe.addsHediff?.addedPartProps is null || !recipe.addsHediff.addedPartProps.betterThanNatural)
            && HealthUtility.PartRemovalIntent(patient, part) == BodyPartRemovalIntent.Harvest;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.CheckAcceptArrest))]
public static class MercenaryArrestAttemptViolationPatch
{
    public static void Prefix(Pawn __instance, Pawn arrester)
    {
        if (__instance is null
            || __instance.Dead
            || arrester?.Faction != Faction.OfPlayer)
        {
            return;
        }

        QuestPart_ClashMercenary? part = ClashMercenaryQuestUtility.FindActivePartForPawn(__instance);
        part?.ReportHostileBehavior();
    }
}

[HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.CapturedBy))]
public static class MercenaryCapturedViolationPatch
{
    private static readonly FieldInfo? PawnField = AccessTools.Field(typeof(Pawn_GuestTracker), "pawn");

    public static void Prefix(Pawn_GuestTracker __instance, Faction by, Pawn byPawn = null!)
    {
        if (by != Faction.OfPlayer && byPawn?.Faction != Faction.OfPlayer)
        {
            return;
        }

        Pawn? pawn = PawnField?.GetValue(__instance) as Pawn;
        if (pawn is null || pawn.Dead)
        {
            return;
        }

        QuestPart_ClashMercenary? part = ClashMercenaryQuestUtility.FindActivePartForPawn(pawn);
        part?.ReportHostileBehavior();
    }
}
