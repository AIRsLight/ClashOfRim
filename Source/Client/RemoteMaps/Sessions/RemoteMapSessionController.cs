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
        if (session is null || !session.IsActive || !ReferenceEquals(session, ClashOfRimGameComponent.ActiveRemoteMapSession))
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

        return RemoteSessionMapUtility.CloseObservationSession(session);
    }

    public static bool TryRequestRaidBattleFinish(
        ClashOfRimMod? mod,
        ActiveRaidBattleSession? session,
        string finishReason,
        bool requireConfirmation = false)
    {
        if (mod is null || !ClientRemoteSessionRuntime.Owns(session) || session.FinishInProgress)
        {
            return false;
        }

        var game = Current.Game;
        string reason = finishReason ?? string.Empty;
        if (requireConfirmation)
        {
            ShowRaidBattleFinishConfirmation(mod, session, reason, game);
            return true;
        }

        return StartRaidBattleFinish(mod, session, reason, game);
    }

    private static void ShowRaidBattleFinishConfirmation(
        ClashOfRimMod mod,
        ActiveRaidBattleSession session,
        string finishReason,
        Game game)
    {
        int playerPawnCount = CountPlayerPawnsOnRaidBattleMap(session);
        string text = playerPawnCount > 0
            ? ClashOfRimText.Key(
                "ClashOfRim.Raid.ConfirmFinishWithPlayerPawns",
                playerPawnCount.Named("COUNT"))
            : ClashOfRimText.Key("ClashOfRim.Raid.ConfirmFinishNoPawns");
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            text,
            () => StartRaidBattleFinish(mod, session, finishReason, game)));
    }

    private static bool StartRaidBattleFinish(
        ClashOfRimMod mod,
        ActiveRaidBattleSession session,
        string reason,
        Game game)
    {
        if (!ReferenceEquals(Current.Game, game) || !ClientRemoteSessionRuntime.Owns(session) || session.FinishInProgress) return false;
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
        => RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session))?.Map;

    public static bool TryBeginRaidBattleFinish(ActiveRaidBattleSession? session) => ClientRemoteSessionRuntime.TryBeginRaidBattleFinish(session);
    public static void KeepRaidBattleFinishInProgress(ActiveRaidBattleSession? session) => ClientRemoteSessionRuntime.KeepRaidBattleFinishInProgress(session);
    public static void ResetRaidBattleFinish(ActiveRaidBattleSession? session) => ClientRemoteSessionRuntime.ResetRaidBattleFinish(session);
    public static bool CloseRaidBattleAfterSettlementAccepted(ActiveRaidBattleSession session, string finishReason)
        => ClientRemoteSessionRuntime.CloseRaidBattleAfterSettlementAccepted(session, finishReason);
    public static bool CloseRaidBattleForExternalResolution(string? eventId, string reason)
        => ClientRemoteSessionRuntime.CloseRaidBattleForExternalResolution(eventId, reason);
    public static bool CloseRaidBattleForFinalDeadline(ActiveRaidBattleSession session)
        => ClientRemoteSessionRuntime.CloseRaidBattleForFinalDeadline(session);
    public static void CloseObservation(RemoteSessionMapParent? mapParent) => ClientRemoteSessionRuntime.CloseObservation(mapParent);
    public static ActiveRemoteMapSession RegisterOpenedObservation(RemoteSessionMapOpenRequest request, RemoteSessionMapOpenResult result)
        => ClientRemoteSessionRuntime.RegisterOpenedObservation(request, result);
    public static ActiveRaidBattleSession CreateRaidBattleSession(string raidEventId, string attackerUserId,
        string attackerColonyId, string attackerSnapshotId, RemoteSessionMapOpenRequest request, RemoteSessionMapOpenResult result,
        string defenderLineageToken, TimeSpan battleDuration, TimeSpan settlementGraceDuration, ModRaidGuardDeploymentDto? guardDeployment = null)
        => ClientRemoteSessionRuntime.CreateRaidBattleSession(raidEventId, attackerUserId, attackerColonyId, attackerSnapshotId,
            request, result, defenderLineageToken, battleDuration, settlementGraceDuration, guardDeployment);
    public static void PauseRaidBattleTicking() => ClientRemoteSessionRuntime.PauseRaidBattleTicking();
}
