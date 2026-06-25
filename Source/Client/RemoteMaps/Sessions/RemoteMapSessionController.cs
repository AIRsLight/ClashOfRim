using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteMapSessionController
{
    public static string PrimaryActionLabel(ActiveRemoteMapSession session)
    {
        return session.IsRaidBattle
            ? ClashOfRimText.Key("ClashOfRim.Raid.FinishBattle")
            : ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Close");
    }

    public static bool TryRunPrimaryAction(
        ClashOfRimMod? mod,
        ActiveRemoteMapSession? session,
        string raidFinishReason,
        bool requireConfirmation = false)
    {
        if (session is null || !session.IsActive)
        {
            return false;
        }

        if (session.IsRaidBattle)
        {
            return TryRequestRaidBattleFinish(
                mod,
                ClashOfRimGameComponent.ActiveRaidBattleSession,
                raidFinishReason,
                requireConfirmation);
        }

        return RemoteSessionMapUtility.CloseActiveObservationMap();
    }

    public static bool TryRequestRaidBattleFinish(
        ClashOfRimMod? mod,
        ActiveRaidBattleSession? session,
        string finishReason,
        bool requireConfirmation = false)
    {
        if (mod is null || session is null || session.FinishInProgress)
        {
            return false;
        }

        string reason = finishReason ?? string.Empty;
        if (requireConfirmation)
        {
            ShowRaidBattleFinishConfirmation(mod, session, reason);
            return true;
        }

        return StartRaidBattleFinish(mod, session, reason);
    }

    private static void ShowRaidBattleFinishConfirmation(
        ClashOfRimMod mod,
        ActiveRaidBattleSession session,
        string finishReason)
    {
        int playerPawnCount = CountPlayerPawnsOnRaidBattleMap(session);
        string text = playerPawnCount > 0
            ? ClashOfRimText.Key(
                "ClashOfRim.Raid.ConfirmFinishWithPlayerPawns",
                playerPawnCount.Named("COUNT"))
            : ClashOfRimText.Key("ClashOfRim.Raid.ConfirmFinishNoPawns");
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            text,
            () => StartRaidBattleFinish(mod, session, finishReason)));
    }

    private static bool StartRaidBattleFinish(
        ClashOfRimMod mod,
        ActiveRaidBattleSession session,
        string reason)
    {
        ClashLog.Message("[ClashOfRim][RemoteSession] Requesting raid battle finish: event="
            + session.EventId
            + ", reason="
            + reason
            + ".");
        mod.StartFinishActiveRaidBattle(session, reason);
        return true;
    }

    private static int CountPlayerPawnsOnRaidBattleMap(ActiveRaidBattleSession session)
    {
        Map? map = FindRaidBattleMap(session);
        if (map?.mapPawns is null || Faction.OfPlayer is null)
        {
            return 0;
        }

        return map.mapPawns.AllPawnsSpawned.Count(pawn =>
            pawn is not null
            && !pawn.Dead
            && !pawn.Destroyed
            && pawn.Faction == Faction.OfPlayer);
    }

    private static Map? FindRaidBattleMap(ActiveRaidBattleSession session)
    {
        string runtimeMapId = string.IsNullOrWhiteSpace(session.ClientMapId)
            ? session.TargetMapId
            : session.ClientMapId;
        if (Find.Maps is null || string.IsNullOrWhiteSpace(runtimeMapId))
        {
            return null;
        }

        return Find.Maps.FirstOrDefault(map =>
            map is not null
            && MapIdsMatch("Map_" + map.uniqueID, runtimeMapId));
    }

    private static bool MapIdsMatch(string currentMapId, string targetMapId)
    {
        if (string.IsNullOrWhiteSpace(currentMapId) || string.IsNullOrWhiteSpace(targetMapId))
        {
            return false;
        }

        string normalizedTarget = targetMapId.StartsWith("Map_", StringComparison.Ordinal)
            ? targetMapId
            : "Map_" + targetMapId;
        return string.Equals(currentMapId, normalizedTarget, StringComparison.Ordinal);
    }

    public static bool TryBeginRaidBattleFinish(ActiveRaidBattleSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId) || session.FinishInProgress)
        {
            return false;
        }

        PauseRaidBattleTicking();
        ClashOfRimGameComponent.MarkRaidBattleFinishInProgress(session.EventId, inProgress: true);
        ClashLog.Message("[ClashOfRim][RemoteSession] Raid battle finish started: event="
            + session.EventId
            + ".");
        return true;
    }

    public static void KeepRaidBattleFinishInProgress(ActiveRaidBattleSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        PauseRaidBattleTicking();
        ClashOfRimGameComponent.MarkRaidBattleFinishInProgress(session.EventId, inProgress: true);
        ClashLog.Message("[ClashOfRim][RemoteSession] Raid battle finish kept in progress: event="
            + session.EventId
            + ".");
    }

    public static void ResetRaidBattleFinish(ActiveRaidBattleSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        ClashOfRimGameComponent.MarkRaidBattleFinishInProgress(session.EventId, inProgress: false);
        ClashLog.Message("[ClashOfRim][RemoteSession] Raid battle finish reset: event="
            + session.EventId
            + ".");
    }

    public static bool CloseRaidBattleAfterSettlementAccepted(ActiveRaidBattleSession session, string finishReason)
    {
        if (session is null)
        {
            return false;
        }

        bool closed = RemoteSessionMapUtility.CloseRaidBattleMap(
            session.EventId,
            "raid settlement accepted: " + finishReason,
            clearActiveSession: false);
        ClashLog.Message("[ClashOfRim][RemoteSession] Closed raid battle after settlement upload was accepted: event="
            + session.EventId
            + ", reason="
            + (finishReason ?? string.Empty)
            + ", closed="
            + closed
            + ".");
        return closed;
    }

    public static bool CloseRaidBattleForExternalResolution(string? eventId, string reason)
    {
        bool closed = RemoteSessionMapUtility.CloseRaidBattleMap(eventId, reason);
        ClashLog.Message("[ClashOfRim][RemoteSession] Closed raid battle for external resolution: event="
            + (eventId ?? string.Empty)
            + ", reason="
            + (reason ?? string.Empty)
            + ", closed="
            + closed
            + ".");
        return closed;
    }

    public static bool CloseRaidBattleForFinalDeadline(ActiveRaidBattleSession session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return false;
        }

        PauseRaidBattleTicking();
        bool closed = RemoteSessionMapUtility.CloseRaidBattleMap(
            session.EventId,
            "raid settlement final deadline expired");
        ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
        ClashLog.Message("[ClashOfRim][RemoteSession] Closed raid battle at final deadline: event="
            + session.EventId
            + ", closed="
            + closed
            + ".");
        return closed;
    }

    public static void CloseObservation(RemoteSessionMapParent? mapParent)
    {
        RemoteSessionMapUtility.CloseObservationMap(mapParent);
    }

    public static ActiveRemoteMapSession RegisterOpenedObservation(
        RemoteSessionMapOpenRequest request,
        RemoteSessionMapOpenResult result)
    {
        ActiveRemoteMapSession session = ActiveRemoteMapSession.FromOpenedObservation(request, result);
        ClashOfRimGameComponent.SetActiveObservationSession(session);
        ClashLog.Message("[ClashOfRim][RemoteSession] Registered observation session: session="
            + session.SessionId
            + ", mode="
            + session.Mode
            + ", target="
            + session.TargetUserId
            + ".");
        return session;
    }

    public static ActiveRaidBattleSession CreateRaidBattleSession(
        string raidEventId,
        string attackerUserId,
        string attackerColonyId,
        string attackerSnapshotId,
        RemoteSessionMapOpenRequest request,
        RemoteSessionMapOpenResult result,
        string defenderLineageToken,
        TimeSpan battleDuration,
        TimeSpan settlementGraceDuration,
        ModRaidGuardDeploymentDto? guardDeployment = null)
    {
        DateTime nowUtc = DateTime.UtcNow;
        ActiveRaidBattleSession session = new()
        {
            EventId = raidEventId,
            AttackerUserId = attackerUserId,
            AttackerColonyId = attackerColonyId,
            AttackerSnapshotId = attackerSnapshotId,
            DefenderUserId = request.Target.OwnerUserId ?? string.Empty,
            DefenderColonyId = request.Target.OwnerColonyId ?? string.Empty,
            DefenderSnapshotId = request.SnapshotId ?? string.Empty,
            DefenderLineageToken = defenderLineageToken ?? string.Empty,
            TargetWorldObjectId = request.Target.WorldObjectId ?? string.Empty,
            TargetMapId = request.Target.MapId ?? string.Empty,
            ClientMapId = result.Map is null ? string.Empty : "Map_" + result.Map.uniqueID,
            TargetTile = request.Target.Tile,
            SavePath = "memory:" + request.SessionId,
            StartedAtUtcTicks = nowUtc.Ticks,
            DeadlineUtcTicks = nowUtc.Add(battleDuration).Ticks,
            FinalDeadlineUtcTicks = nowUtc.Add(battleDuration).Add(settlementGraceDuration).Ticks,
            GuardDeploymentId = guardDeployment?.ContractId ?? string.Empty,
            GuardDeploymentTier = guardDeployment?.Tier ?? string.Empty,
            GuardDeploymentPoints = Math.Max(0, guardDeployment?.Points ?? 0),
            GuardDeploymentSeed = guardDeployment?.Seed ?? 0
        };
        ClashLog.Message("[ClashOfRim][RemoteSession] Created raid battle session: event="
            + session.EventId
            + ", defender="
            + session.DefenderUserId
            + ", clientMap="
            + session.ClientMapId
            + ".");
        return session;
    }

    public static void PauseRaidBattleTicking()
    {
        if (Find.TickManager is not null)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }
    }
}
