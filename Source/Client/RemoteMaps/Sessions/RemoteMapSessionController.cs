using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;

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
        => session?.IsRaidBattle == true && mod is null ? false : TryRunPrimaryAction(session, raidFinishReason, requireConfirmation);

    public static bool TryRunPrimaryAction(ActiveRemoteMapSession? session, string raidFinishReason, bool requireConfirmation = false)
    {
        if (session is null || !session.IsActive || !ReferenceEquals(session, ClashOfRimGameComponent.ActiveRemoteMapSession))
        {
            return false;
        }

        if (session.IsRaidBattle)
        {
            return TryRequestRaidBattleFinish(
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
        => mod is not null && TryRequestRaidBattleFinish(session, finishReason, requireConfirmation);

    public static bool TryRequestRaidBattleFinish(ActiveRaidBattleSession? session, string finishReason, bool requireConfirmation = false)
        => ClientRaidFinishActions.Current?.Request(session, finishReason, requireConfirmation) == true;

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
