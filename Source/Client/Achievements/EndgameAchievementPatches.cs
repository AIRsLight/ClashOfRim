using System;
using System.Reflection;
using AIRsLight.ClashOfRim.Modules.Achievements;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class EndgameAchievementRuntime
{
    internal static bool ConfirmOrDefer(string key, Action continuation)
        => ClashOfRimMod.Instance?.ConfirmEndgameOrDefer(key, continuation) ?? true;
    internal static void Confirm(ClientEndgameKind kind, string eventKey)
    {
        if (string.IsNullOrWhiteSpace(eventKey)) return;
        ClashOfRimMod.Instance?.StartEndgameAchievementSnapshotConfirmation(new[]
        { ClientEndgameAchievements.Candidate(kind, eventKey) });
    }
}

[HarmonyPatch(typeof(ShipCountdown), nameof(ShipCountdown.InitiateCountdown), typeof(Building))]
public static class ClashOfRimShipCountdownBuildingPatch
{
    public static bool Prefix(Building launchingShipRoot)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "ship-building",
            () => ShipCountdown.InitiateCountdown(launchingShipRoot));
    }
}

[HarmonyPatch(typeof(ShipCountdown), nameof(ShipCountdown.InitiateCountdown), typeof(string))]
public static class ClashOfRimShipCountdownStringPatch
{
    public static bool Prefix(string launchString)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "ship-string",
            () => ShipCountdown.InitiateCountdown(launchString));
    }
}

[HarmonyPatch(typeof(ShipCountdown), "CountdownEnded")]
public static class ClashOfRimShipCountdownEndedPatch
{
    private static readonly FieldInfo? ShipRootField = AccessTools.Field(typeof(ShipCountdown), "shipRoot");
    public static void Prefix(out string? __state)
    {
        __state = ShipRootField?.GetValue(null) is Building root
            ? "ship:" + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + root.ThingID : null;
    }
    public static void Postfix(bool __runOriginal, string? __state)
    {
        if (__runOriginal && __state is not null) EndgameAchievementRuntime.Confirm(ClientEndgameKind.Ship, __state);
    }
}

[HarmonyPatch(typeof(ArchonexusCountdown), nameof(ArchonexusCountdown.InitiateCountdown))]
public static class ClashOfRimArchonexusCountdownPatch
{
    public static bool Prefix(Building_ArchonexusCore archonexusCore)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "archonexus",
            () => ArchonexusCountdown.InitiateCountdown(archonexusCore));
    }
}

[HarmonyPatch(typeof(ArchonexusCountdown), "EndGame")]
public static class ClashOfRimArchonexusEndGamePatch
{
    public static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        EndgameAchievementRuntime.Confirm(ClientEndgameKind.Archonexus,
            "archonexus:" + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

[HarmonyPatch(typeof(VoidAwakeningUtility), nameof(VoidAwakeningUtility.EmbraceTheVoid))]
public static class ClashOfRimEmbraceTheVoidPatch
{
    public static bool Prefix(Pawn pawn)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "void-embrace",
            () => VoidAwakeningUtility.EmbraceTheVoid(pawn));
    }

    public static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        EndgameAchievementRuntime.Confirm(ClientEndgameKind.VoidEmbrace,
            "void-embrace:" + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

[HarmonyPatch(typeof(VoidAwakeningUtility), nameof(VoidAwakeningUtility.DisruptTheLink))]
public static class ClashOfRimDisruptTheLinkPatch
{
    public static bool Prefix(Pawn pawn)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "void-disrupt",
            () => VoidAwakeningUtility.DisruptTheLink(pawn));
    }

    public static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        EndgameAchievementRuntime.Confirm(ClientEndgameKind.VoidDisrupt,
            "void-disrupt:" + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

[HarmonyPatch(typeof(CompCerebrexCore), "StartCoreDeactivation")]
public static class ClashOfRimCerebrexStartCoreDeactivationPatch
{
    public static bool Prefix(CompCerebrexCore __instance, bool scavenge)
    {
        return EndgameAchievementRuntime.ConfirmOrDefer(
            "cerebrex-" + scavenge,
            () => AccessTools.Method(typeof(CompCerebrexCore), "StartCoreDeactivation")?.Invoke(__instance, new object[] { scavenge }));
    }
}

[HarmonyPatch(typeof(CompCerebrexCore), "DeactivateCore")]
public static class ClashOfRimCerebrexDeactivateCorePatch
{
    public static void Postfix(bool scavenging, bool __runOriginal)
    {
        if (!__runOriginal) return;
        EndgameAchievementRuntime.Confirm(
            scavenging ? ClientEndgameKind.CerebrexScavenged : ClientEndgameKind.CerebrexDestroyed,
            "cerebrex:" + (scavenging ? "scavenged:" : "destroyed:") + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
