using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.Raids;

[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
public static class RaidDefenderLootProtectionDeathPatch
{
    public static void Prefix(Pawn __instance)
    {
        RaidDefenderLootProtectionUtility.DestroyProtectedGear(__instance);
    }
}

[HarmonyPatch]
public static class RaidDefenderLootProtectionEquipmentDropPatch
{
    public static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(Pawn_EquipmentTracker),
            nameof(Pawn_EquipmentTracker.TryDropEquipment),
            new[] { typeof(ThingWithComps), typeof(ThingWithComps).MakeByRefType(), typeof(IntVec3), typeof(bool) });
    }

    public static bool Prefix(
        Pawn_EquipmentTracker __instance,
        ThingWithComps eq,
        ref ThingWithComps resultingEq,
        ref bool __result)
    {
        if (!RaidDefenderLootProtectionUtility.TryDissolveEquipment(__instance.pawn, eq))
        {
            return true;
        }

        resultingEq = null!;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Remove))]
public static class RaidDefenderLootProtectionEquipmentRemovePatch
{
    public static bool Prefix(Pawn_EquipmentTracker __instance, ThingWithComps eq)
    {
        return !RaidDefenderLootProtectionUtility.TryDissolveEquipment(__instance.pawn, eq);
    }
}

[HarmonyPatch]
public static class RaidDefenderLootProtectionApparelDropPatch
{
    public static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(Pawn_ApparelTracker),
            nameof(Pawn_ApparelTracker.TryDrop),
            new[] { typeof(Apparel), typeof(Apparel).MakeByRefType(), typeof(IntVec3), typeof(bool) });
    }

    public static bool Prefix(
        Pawn_ApparelTracker __instance,
        Apparel ap,
        ref Apparel resultingAp,
        ref bool __result)
    {
        if (!RaidDefenderLootProtectionUtility.TryDissolveApparel(__instance.pawn, ap))
        {
            return true;
        }

        resultingAp = null!;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Remove))]
public static class RaidDefenderLootProtectionApparelRemovePatch
{
    public static bool Prefix(Pawn_ApparelTracker __instance, Apparel ap)
    {
        return !RaidDefenderLootProtectionUtility.TryDissolveApparel(__instance.pawn, ap);
    }
}

[HarmonyPatch(typeof(FloatMenuOptionProvider_CapturePawn), "GetSingleOptionFor", new[] { typeof(Pawn), typeof(FloatMenuContext) })]
public static class RaidDefenderLootProtectionCaptureMenuPatch
{
    public static bool Prefix(Pawn clickedPawn, ref FloatMenuOption __result)
    {
        if (!RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(clickedPawn))
        {
            return true;
        }

        __result = RaidDefenderLootProtectionMenuUtility.BlockedCaptureOption();
        return false;
    }
}

[HarmonyPatch(typeof(FloatMenuOptionProvider_Arrest), "GetSingleOptionFor", new[] { typeof(Pawn), typeof(FloatMenuContext) })]
public static class RaidDefenderLootProtectionArrestMenuPatch
{
    public static bool Prefix(Pawn clickedPawn, ref FloatMenuOption __result)
    {
        if (!RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(clickedPawn))
        {
            return true;
        }

        __result = RaidDefenderLootProtectionMenuUtility.BlockedArrestOption();
        return false;
    }
}

internal static class RaidDefenderLootProtectionMenuUtility
{
    public static FloatMenuOption BlockedCaptureOption()
    {
        return BlockedOption("CannotCapture");
    }

    public static FloatMenuOption BlockedArrestOption()
    {
        return BlockedOption("CannotArrest");
    }

    private static FloatMenuOption BlockedOption(string vanillaCannotKey)
    {
        string label = vanillaCannotKey.Translate() + ": "
            + ClashOfRimText.Key("ClashOfRim.Raid.CannotCaptureProtectedDefender");
        return new FloatMenuOption(label, null, MenuOptionPriority.Default);
    }
}

[HarmonyPatch(typeof(CaptureUtility), nameof(CaptureUtility.CanArrest))]
public static class RaidDefenderLootProtectionArrestValidationPatch
{
    public static void Postfix(Pawn victim, ref string reason, ref bool __result)
    {
        if (!RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(victim))
        {
            return;
        }

        reason = ClashOfRimText.Key("ClashOfRim.Raid.CannotCaptureProtectedDefender");
        __result = false;
    }
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
public static class RaidDefenderLootProtectionOrderedCaptureJobPatch
{
    public static bool Prefix(Job job, ref bool __result)
    {
        if (!IsBlockedProtectedDefenderJob(job))
        {
            return true;
        }

        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.Raid.CannotCaptureProtectedDefender"),
            MessageTypeDefOf.RejectInput,
            historical: false);
        __result = false;
        return false;
    }

    internal static bool IsBlockedProtectedDefenderJob(Job? job)
    {
        if (job?.def is null)
        {
            return false;
        }

        return IsProtectedCaptureJob(job);
    }

    private static bool IsProtectedCaptureJob(Job job)
    {
        return IsCaptureJobDef(job.def)
            && (RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(job.targetA.Thing as Pawn)
                || RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(job.targetB.Thing as Pawn)
                || RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn(job.targetC.Thing as Pawn));
    }

    private static bool IsCaptureJobDef(JobDef def)
    {
        return def == JobDefOf.Capture
            || def == JobDefOf.Arrest
            || def == JobDefOf.CarryToPrisonerBedDrafted
            || def == JobDefOf.Kidnap;
    }
}

[HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing) })]
public static class RaidDefenderLootProtectionCarryPawnPatch
{
    public static bool Prefix(Pawn_CarryTracker __instance, Thing item, ref bool __result)
    {
        if (!RaidDefenderLootProtectionTransportUtility.ShouldBlockPlayerTransport(__instance, item))
        {
            return true;
        }

        RaidDefenderLootProtectionTransportUtility.NotifyBlocked();
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing), typeof(int), typeof(bool) })]
public static class RaidDefenderLootProtectionCarryThingPatch
{
    public static bool Prefix(Pawn_CarryTracker __instance, Thing item, ref int __result)
    {
        if (!RaidDefenderLootProtectionTransportUtility.ShouldBlockPlayerTransport(__instance, item))
        {
            return true;
        }

        RaidDefenderLootProtectionTransportUtility.NotifyBlocked();
        __result = 0;
        return false;
    }
}

[HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.AllSendablePawns))]
public static class RaidDefenderLootProtectionReformPawnListPatch
{
    public static void Postfix(ref List<Pawn> __result)
    {
        __result?.RemoveAll(RaidDefenderLootProtectionUtility.IsProtectedDefenderPawn);
    }
}

[HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllReachableColonyItems))]
public static class RaidDefenderLootProtectionReformItemListPatch
{
    public static void Postfix(ref List<Thing> __result)
    {
        __result?.RemoveAll(RaidDefenderLootProtectionUtility.IsProtectedDefenderPawnOrCorpse);
    }
}

internal static class RaidDefenderLootProtectionTransportUtility
{
    public static bool ShouldBlockPlayerTransport(Pawn_CarryTracker? carryTracker, Thing? thing)
    {
        return carryTracker?.pawn?.Faction == Faction.OfPlayer
            && RaidDefenderLootProtectionUtility.IsProtectedDefenderPawnOrCorpse(thing);
    }

    public static void NotifyBlocked()
    {
        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.Raid.CannotCaptureProtectedDefender"),
            MessageTypeDefOf.RejectInput,
            historical: false);
    }
}
