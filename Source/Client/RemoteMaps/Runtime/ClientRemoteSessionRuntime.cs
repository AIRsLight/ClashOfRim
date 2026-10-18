using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientRemoteSessionRuntime
{
    internal static bool Owns([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ActiveRaidBattleSession? session)
        => session is not null && !string.IsNullOrWhiteSpace(session.EventId)
            && ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, session);

    public static bool TryBeginRaidBattleFinish(ActiveRaidBattleSession? session)
    {
        if (!Owns(session) || session.FinishInProgress)
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
        if (!Owns(session))
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
        if (!Owns(session))
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
        if (!Owns(session))
        {
            return false;
        }

        var game = Current.Game;
        bool closed = RemoteSessionMapUtility.CloseRaidBattleSession(
            session,
            "raid settlement accepted: " + finishReason,
            clearActiveSession: false);
        if (ReferenceEquals(Current.Game, game) && Owns(session)) RequireMapClosed(session, closed);
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
        if (!Owns(session))
        {
            return false;
        }

        var game = Current.Game;
        PauseRaidBattleTicking();
        bool closed = RemoteSessionMapUtility.CloseRaidBattleSession(
            session,
            "raid settlement final deadline expired", clearActiveSession: true);
        if (ReferenceEquals(Current.Game, game) && Owns(session))
        {
            // Expiry continues to the authoritative reload even if local unloading failed.
            if (closed || RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session))?.Map is null)
                ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
        }
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
    private static void RequireMapClosed(ActiveRaidBattleSession session, bool closed)
    {
        if (!closed && RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session))?.Map is not null)
            throw new InvalidOperationException("The raid map could not be unloaded; its session was retained for recovery.");
    }
}
