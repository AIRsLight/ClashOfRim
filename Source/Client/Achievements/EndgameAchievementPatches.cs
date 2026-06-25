using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;

namespace AIRsLight.ClashOfRim;

internal static class EndgameAchievementRuntime
{
    private const string CategoryEndgame = "Endgame";
    private const string AggregationSum = "Sum";
    private const string ColorPurple = "Purple";
    private const string ColorRed = "Red";
    internal const string EndgameConfirmationOperation = "EndgameAchievement";
    private static readonly HashSet<string> ConfirmedContinuations = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool ConfirmOrDefer(string key, Action continuation)
    {
        ClashOfRimMod? mod = ClashOfRimMod.Instance;
        if (mod?.IsInActiveMultiplayerSession != true)
        {
            return true;
        }

        lock (Gate)
        {
            if (ConfirmedContinuations.Remove(key))
            {
                return true;
            }
        }

        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            ClashOfRimText.Key("ClashOfRim.Endgame.ConfirmationText"),
            () =>
            {
                lock (Gate)
                {
                    ConfirmedContinuations.Add(key);
                }

                continuation();
            }));
        return false;
    }

    public static void ConfirmShipLaunch(string eventKey)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
        {
            return;
        }

        List<ModSnapshotAchievementCandidateDto> candidates = new()
        {
            BuildCandidate(
                "endgame_ship_launch",
                eventKey + ":launch",
                1,
                "ClashOfRim.Achievement.EndgameShipLaunch",
                "endgame.ship.launch",
                ColorPurple)
        };
        ClashOfRimMod.Instance?.StartEndgameAchievementSnapshotConfirmation(candidates);
    }

    public static void ConfirmSimpleEndgame(string achievementId, string labelKey, string iconId, string eventKey)
    {
        ClashOfRimMod.Instance?.StartEndgameAchievementSnapshotConfirmation(new[]
        {
            BuildCandidate(achievementId, eventKey, 1, labelKey, iconId, ColorRed)
        });
    }

    private static ModSnapshotAchievementCandidateDto BuildCandidate(
        string achievementId,
        string eventKey,
        long value,
        string labelKey,
        string iconId,
        string color)
    {
        return new ModSnapshotAchievementCandidateDto
        {
            AchievementId = achievementId,
            EventKey = eventKey,
            Value = value,
            Category = CategoryEndgame,
            LabelKey = labelKey,
            IconId = iconId,
            Color = color,
            AggregationKind = AggregationSum
        };
    }
}

public sealed partial class ClashOfRimMod
{
    internal void StartEndgameAchievementSnapshotConfirmation(
        IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
    {
        if (!IsInActiveMultiplayerSession || candidates.Count == 0)
        {
            return;
        }

        ClashOfRimGameComponent.EnqueuePendingAchievementCandidates(candidates);
        if (ClashOfRimGameComponent.CopyPendingAchievementCandidates().Count == 0)
        {
            return;
        }

        string operation = ClashOfRimText.Key("ClashOfRim.Endgame.UploadStarted");
        if (TryRejectBlockedByLocalAtomicMutation(out string blocked))
        {
            NotifyPlayerMessage(blocked, MessageTypeDefOf.RejectInput);
            return;
        }

        BeginLocalAtomicMutation(operation, operation);
        if (!TryBeginSnapshotUploadTransaction())
        {
            ClearLocalAtomicMutation();
            NotifyPlayerMessage(ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy"), MessageTypeDefOf.RejectInput);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var service = new ModSnapshotUploadService(settings);
                ModSnapshotUploadResult result = await service.UploadConfiguredSnapshotAsync(
                    removeRaidBattleSessions: false,
                    confirmationOperation: EndgameAchievementRuntime.EndgameConfirmationOperation,
                    snapshotUploadKind: ModSnapshotUploadKinds.EndgameAchievement);
                if (result.Success)
                {
                    snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Endgame.UploadSucceeded");
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.PositiveEvent);
                    StartRefreshAchievements();
                }
                else
                {
                    string reason = result.Message ?? result.ErrorCode ?? string.Empty;
                    snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Endgame.UploadFailed", reason.Named("REASON"));
                    NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                    Log.Warning("[ClashOfRim] Endgame achievement snapshot confirmation failed: " + result.ErrorCode + " " + result.Message);
                }
            }
            catch (Exception ex)
            {
                snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Endgame.UploadFailed", ex.Message.Named("REASON"));
                NotifyPlayerMessage(snapshotUploadStatus, MessageTypeDefOf.RejectInput);
                Log.Error("[ClashOfRim] Endgame achievement snapshot confirmation exception: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                CompleteLocalAtomicMutation();
            }
        });
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
    private static bool shipLaunchObserved;
    private static string eventKey = string.Empty;

    public static void Prefix()
    {
        shipLaunchObserved = false;
        eventKey = "ship:" + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ShipRootField?.GetValue(null) is not Building shipRoot)
        {
            return;
        }

        shipLaunchObserved = true;
        eventKey += ":" + shipRoot.ThingID;
    }

    public static void Postfix()
    {
        if (shipLaunchObserved)
        {
            EndgameAchievementRuntime.ConfirmShipLaunch(eventKey);
        }
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
    public static void Postfix()
    {
        EndgameAchievementRuntime.ConfirmSimpleEndgame(
            "endgame_archonexus",
            "ClashOfRim.Achievement.EndgameArchonexus",
            "endgame.archonexus",
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

    public static void Postfix()
    {
        EndgameAchievementRuntime.ConfirmSimpleEndgame(
            "endgame_void_embrace",
            "ClashOfRim.Achievement.EndgameVoid",
            "endgame.void",
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

    public static void Postfix()
    {
        EndgameAchievementRuntime.ConfirmSimpleEndgame(
            "endgame_void_disrupt",
            "ClashOfRim.Achievement.EndgameVoid",
            "endgame.void",
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
    public static void Postfix(bool scavenging)
    {
        EndgameAchievementRuntime.ConfirmSimpleEndgame(
            scavenging ? "endgame_cerebrex_scavenged" : "endgame_cerebrex_destroyed",
            "ClashOfRim.Achievement.EndgameCerebrex",
            "endgame.cerebrex",
            "cerebrex:" + (scavenging ? "scavenged:" : "destroyed:") + GenTicks.TicksGame.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
