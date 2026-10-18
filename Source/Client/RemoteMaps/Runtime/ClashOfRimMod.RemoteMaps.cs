using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Events;
using static AIRsLight.ClashOfRim.Modules.Maps.ClientMapIdentity;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private const int RaidAttackerSnapshotRetryDelayMs = 250;
    private const int RaidAttackerSnapshotWaitLogInterval = 20;

    private static void QueueRemoteMapLongEvent(string textKey, Action action)
    {
        CloseServerEntryProgressWindowNow();
        ClientRemoteMapLongEvent.Enqueue(action, static () => { }, textKey);
    }

    private static void SetRemoteMapLongEventText(string textKey)
    {
        LongEventHandler.SetCurrentEventText(ClashOfRimText.Key(textKey));
    }

    internal void OpenCaravanRaidMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (targets.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoTargets");
            return;
        }

        IReadOnlyList<Pawn> pawns = new CaravanRaidAttackForceSource(caravan).AttackPawns;
        if (pawns.Count == 0)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusNoPawns");
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (ModWorldMapMarkerDto target in targets)
        {
            ModWorldMapMarkerDto captured = target;
            options.Add(new FloatMenuOption(
                ClashOfRimText.Key(
                    "ClashOfRim.Raid.CaravanTargetOption",
                    FormatSupportTargetLabel(captured).Named("TARGET"),
                    pawns.Count.Named("COUNT")),
                () =>
                {
                    string confirmation = ClashOfRimText.Key(
                        "ClashOfRim.Raid.ConfirmStart",
                        FormatSupportTargetLabel(captured).Named("TARGET"),
                        pawns.Count.Named("COUNT"));
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        confirmation,
                        () => StartCreateRaidFromCaravan(caravan, captured)));
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    public void StartRaidFromVehicleLanding(Caravan caravan, ModWorldMapMarkerDto target)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (caravan is null || caravan.Destroyed)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidStartFailedNoCaravan");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (target is null || !target.CanRaid)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        StartCreateRaidFromCaravan(caravan, target);
    }

    private void StartCreateRaidFromCaravan(Caravan caravan, ModWorldMapMarkerDto target)
    {
        _ = raidLaunching.StartAsync(new ClientRaidLaunchRuntime(new CaravanRaidAttackForceSource(caravan)), target);
    }

    internal void StartCreateRaidFromTransporters(
        string transporterKey,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string targetMapId,
        string targetWorldObjectId,
        PlanetTile targetTile,
        int targetTileLayerId,
        string targetLabel,
        List<ActiveTransporterInfo> transporters)
    {
        var target = new ModWorldMapMarkerDto
        {
            MarkerId = string.Empty,
            Kind = "TradeableColony",
            OwnerUserId = targetUserId ?? string.Empty,
            OwnerColonyId = targetColonyId ?? string.Empty,
            SnapshotId = targetSnapshotId ?? string.Empty,
            MapId = targetMapId ?? string.Empty,
            WorldObjectId = targetWorldObjectId ?? string.Empty,
            Tile = targetTile,
            TileLayerId = Math.Max(0, targetTileLayerId),
            Label = targetLabel ?? string.Empty,
            CanRaid = true
        };
        _ = raidLaunching.StartAsync(new ClientRaidLaunchRuntime(new TransporterRaidAttackForceSource(
            transporters, target.Tile, transporterKey, target.TileLayerId)), target);
    }

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
        => observationMaps.OpenAsync(target, mode);

    internal void StartFinishActiveRaidBattle(ActiveRaidBattleSession session, string finishReason)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        if (!RemoteMapSessionController.TryBeginRaidBattleFinish(session))
        {
            return;
        }

        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementUploading");
        var context = new RaidBattleSettlementContext(session, finishReason);

        StartRaidSettlementSupportPhase(context);
    }

    private void ContinueFinishActiveRaidBattleAfterSupportResolution(RaidBattleSettlementContext context, ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        ActiveRaidBattleSession session = context.Session;
        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            return;
        }

        SetRemoteMapLongEventText("ClashOfRim.Raid.StatusSettlementSavingLong");
        if (!snapshotPreparation.TryBuild(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            Log.Warning("[ClashOfRim][Raid] Failed to build raid settlement snapshot: event="
                + session.EventId
                + ", reason="
                + buildFailureReason
                + ", buildCode="
                + (build.ErrorCode ?? string.Empty)
                + ", buildMessage="
                + (build.Message ?? string.Empty)
                + ".");
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementSaveFailed",
                buildFailureReason.Named("REASON"));
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            uploadTransaction.Dispose();
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.Raid,
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
            return;
        }

        if (!TryPrepareRaidSettlementSnapshotPackage(
                build,
                session,
                out ModSnapshotPackageMetadataDto? confirmedSnapshot,
                out byte[]? confirmedPayload,
                out string prepareFailureReason))
        {
            Log.Warning("[ClashOfRim][Raid] Failed to prepare returned raid settlement snapshot: event="
                + session.EventId
                + ", reason="
                + prepareFailureReason
                + ".");
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusSettlementSaveFailed",
                prepareFailureReason.Named("REASON"));
            RemoteMapSessionController.ResetRaidBattleFinish(session);
            uploadTransaction.Dispose();
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.Raid,
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
            return;
        }

        StartUploadPreparedRaidSettlement(
            context.WithPreparedSnapshot(confirmedSnapshot!, confirmedPayload!),
            continuation: uploadTransaction);
    }

    private static bool TryPrepareRaidSettlementSnapshotPackage(
        ModSnapshotPackageBuildResult build,
        ActiveRaidBattleSession session,
        out ModSnapshotPackageMetadataDto? confirmedSnapshot,
        out byte[]? confirmedPayload,
        out string failureReason)
    {
        confirmedSnapshot = null;
        confirmedPayload = null;
        failureReason = string.Empty;

        byte[] originalPayload = build.OriginalPayload ?? Array.Empty<byte>();
        if (!RaidBattleSnapshotPreparer.TryPrepareReturnedSnapshot(
                originalPayload,
                session,
                out byte[] preparedSaveBytes,
                out failureReason))
        {
            return false;
        }

        ModSnapshotPackageBuildResult prepared = ModSnapshotPackageBuilder.FromSaveBytes(
            preparedSaveBytes,
            "raid-settlement",
            build.Package?.OwnerId ?? string.Empty,
            build.Package?.ColonyId ?? string.Empty,
            DateTime.UtcNow);
        if (!prepared.Success || prepared.Package is null || prepared.Payload is null)
        {
            failureReason = $"{prepared.ErrorCode} {prepared.Message}";
            return false;
        }

        confirmedSnapshot = prepared.Package;
        confirmedPayload = prepared.Payload;
        return true;
    }

    private void StartUploadPreparedRaidSettlement(
        RaidBattleSettlementContext context,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        ActiveRaidBattleSession session = context.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            continuation?.Dispose();
            return;
        }

        ModSnapshotPackageMetadataDto? confirmedSnapshot = context.ConfirmedSnapshot;
        byte[]? confirmedPayload = context.ConfirmedPayload;
        if (confirmedSnapshot is null || confirmedPayload is null)
        {
            continuation?.Dispose();
            return;
        }

        RemoteMapSessionController.KeepRaidBattleFinishInProgress(session);

        var command = new ClientEventConfirmationCommand(context.DefenderIdempotencyKey, session.EventId, null,
            settings.UserId, settings.ColonyId, settings.CurrentSnapshotId, confirmedSnapshot, confirmedPayload,
            context.DefenderClientApplicationResult, RaidSettlementHttpTimeout(session));
        _ = eventConfirmations.PreparedRaidAsync(command, continuation,
            () => StartUploadPreparedRaidSettlement(context),
            (retry, allowRetry) => ShowRaidSettlementFailureNow(context, retry, allowRetry),
            response =>
            {
                if (!string.Equals(
                        ClashOfRimGameComponent.ActiveRaidBattleSession?.EventId,
                        session.EventId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusSettlementSucceeded",
                    response.AppliedSnapshotId.Named("SNAPSHOT"));
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Raid.SettlementSucceededMessage"),
                    MessageTypeDefOf.PositiveEvent,
                    historical: false);
                RemoteMapSessionController.CloseRaidBattleAfterSettlementAccepted(session, context.FinishReason);
                ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
                StartConfirmRaidAttackerSnapshotWhenReady(context);
            });
    }

    private void StartConfirmRaidAttackerSnapshotWhenReady(RaidBattleSettlementContext context, int attempts = 0)
    {
        if (SnapshotUploadInProgress)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotWaiting");
            if (attempts == 0 || attempts % RaidAttackerSnapshotWaitLogInterval == 0)
            {
                ClashLog.Message("[ClashOfRim][Raid] Waiting for settlement upload transaction before attacker snapshot: event="
                    + context.Session.EventId
                    + ", attempts="
                    + attempts
                    + ".");
            }

            ClientSessionWork.Run(async () =>
            {
                await ClientSessionWork.Observe(Task.Delay(RaidAttackerSnapshotRetryDelayMs));
                using var publication = ClientSessionWork.EnterPublication();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    StartConfirmRaidAttackerSnapshotWhenReady(context, attempts + 1));
            });
            return;
        }

        StartConfirmRaidAttackerSnapshot(context);
    }

    private void StartConfirmRaidAttackerSnapshot(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        StartConfirmLocalMutationSnapshot(new ClientLocalSnapshotRequest
        {
            Operation = LocalMutationKind.RaidAttacker,
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotUploading"),
            RemoveRaidBattleSessions = true,
            BestEffort = true,
            SetStatus = value => worldMapStatus = value,
            BuildFailureStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotFailed",
                upload.ErrorCode.Named("CODE"),
                upload.Message.Named("MESSAGE")),
            BuildSuccessStatus = upload => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotSucceeded",
                upload.AcceptedSnapshotId.Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnSuccessOnMainThread = upload =>
            {
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Raid.AttackerSnapshotSucceededMessage"),
                    MessageTypeDefOf.PositiveEvent,
                    historical: false);
                ClashLog.Message("[ClashOfRim][Raid] Attacker cleanup snapshot accepted without server snapshot reload: event="
                    + session.EventId
                    + ", snapshot="
                    + (upload.AcceptedSnapshotId ?? string.Empty)
                    + ".");
                StartRefreshPlayers(ClashOfRimText.Key("ClashOfRim.Raid.AttackerSnapshotSucceededMessage"), requireManualGate: false);
            }
        });
    }

    // Temporary sequencing until the remaining settlement snapshot/close phases
    // move into their module. Loss reporting and pawn ownership live in the dependency.
    private void StartRaidSettlementSupportPhase(RaidBattleSettlementContext context, ClientSnapshotTransaction.Attempt? original = null,
        ClientRaidSupportLosses.Batch? originalBatch = null)
    {
        var admitted = original is null ? mutationAdmission.Begin() : mutationAdmission.Retry(original, LocalMutationKind.Raid);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return;
        if (admitted.Attempt is not { } attempt)
        {
            worldMapStatus = admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged
                ? ClashOfRimText.Key("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged")
                : admitted.Failure == ClientSnapshotAdmissionFailure.Busy ? ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy")
                : ClientLocalMutationPresentation.AdmissionFailure(admitted.Admission, localMutations.Current);
            RemoteMapSessionController.ResetRaidBattleFinish(context.Session);
            if (original is null) Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            else ShowUnconfirmedSnapshotFailure(LocalMutationKind.Raid, worldMapStatus,
                () => StartRaidSettlementSupportPhase(context, original, originalBatch), admitted.Failure == ClientSnapshotAdmissionFailure.Busy);
            return;
        }
        RemoteMapSessionController.KeepRaidBattleFinishInProgress(context.Session);
        _ = ResolveRaidSettlementSupportAsync(context, attempt, originalBatch?.WithAdmission(attempt.Admission));
    }

    private async Task ResolveRaidSettlementSupportAsync(RaidBattleSettlementContext context, ClientSnapshotTransaction.Attempt attempt,
        ClientRaidSupportLosses.Batch? batch)
    {
        try
        {
            batch ??= raidSupportLosses.Capture(attempt, new ClientRaidSupportLossSource(context.Session));
            if (batch is null) return;
            if (batch.Count > 0) worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementReportingSupportLoss", batch.Count.Named("COUNT"));
            var result = await raidSupportLosses.ResolveAsync(batch).ConfigureAwait(false);
            Task? settlement = null;
            await AIRsLight.ClashOfRim.Modules.Sessions.ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                action => snapshotTransactions.TryApply(attempt, action), () =>
                {
                    if (result.Failure is { } failure)
                    { FinishRaidSupportFailure(context, attempt, failure, batch); return; }
                    if (!result.Success) return;
                    CloseServerEntryProgressWindowNow();
                    settlement = AIRsLight.ClashOfRim.Modules.Sessions.ClientGamePublication.EnqueueAsync(
                        (run, discarded) => ClientRemoteMapLongEvent.Enqueue(run, discarded, "ClashOfRim.Raid.StatusSettlementSavingLong"),
                        attempt.Owner.Token, action => snapshotTransactions.TryApply(attempt, action),
                        () => ContinueFinishActiveRaidBattleAfterSupportResolution(context, attempt.Admission));
                }).ConfigureAwait(false);
            if (settlement is not null) await settlement.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ClientSessionWork.IsCurrent || attempt.Owner.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await AIRsLight.ClashOfRim.Modules.Sessions.ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                action => snapshotTransactions.TryApply(attempt, action), () => FinishRaidSupportFailure(context, attempt,
                    new ClientRaidSupportLossFailure(ClientRaidSupportLossFailureKind.Exception, Exception: ex), batch)).ConfigureAwait(false);
        }
        finally { attempt.Admission.Dispose(); }
    }

    private void FinishRaidSupportFailure(RaidBattleSettlementContext context, ClientSnapshotTransaction.Attempt attempt,
        ClientRaidSupportLossFailure failure, ClientRaidSupportLosses.Batch? batch)
    {
        if (!ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, context.Session)) return;
        if (context.Session.IsFinalDeadlineExpired) { HandleRaidBattleFinalDeadlineExpired(context.Session); return; }
        worldMapStatus = ClientRaidSupportLossPresentation.Failure(failure);
        RemoteMapSessionController.ResetRaidBattleFinish(context.Session);
        ShowUnconfirmedSnapshotFailure(LocalMutationKind.Raid, worldMapStatus,
            () => StartRaidSettlementSupportPhase(context, attempt, batch), failure.CanRetry);
        if (failure.Exception is not null) Log.Warning("[ClashOfRim] Raid support loss failed: " + failure.Exception);
    }

    private void ShowRaidSettlementFailure(RaidBattleSettlementContext context, Action? retryAction = null, bool allowRetry = false)
        => ClashOfRimGameComponent.EnqueueMainThreadAction(() => ShowRaidSettlementFailureNow(context, retryAction, allowRetry));

    private void ShowRaidSettlementFailureNow(RaidBattleSettlementContext context, Action? retryAction, bool allowRetry)
    {
        ActiveRaidBattleSession session = context.Session;
        if (session.IsFinalDeadlineExpired)
        {
            HandleRaidBattleFinalDeadlineExpired(session);
            return;
        }

        RemoteMapSessionController.ResetRaidBattleFinish(session);
        ShowUnconfirmedSnapshotFailure(LocalMutationKind.Raid, worldMapStatus,
            retryAction ?? (() => StartFinishActiveRaidBattle(session, context.FinishReason)), allowRetry);
    }

    internal void HandleRaidBattleFinalDeadlineExpired(ActiveRaidBattleSession session, bool closeLocalBattleMap = true)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        if (!finalDeadlineRaidCleanupEventIds.Add(session.EventId))
        {
            ClashLog.Message("[ClashOfRim][Raid] Ignoring duplicate final-deadline handling for active raid "
                + session.EventId
                + ".");
            return;
        }

        if (closeLocalBattleMap)
        {
            RemoteMapSessionController.CloseRaidBattleForFinalDeadline(session);
        }
        else
        {
            ClashOfRimGameComponent.ClearActiveRaidBattleSession(session.EventId);
        }

        CloseUnconfirmedSnapshotFailureWindow();
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementFinalDeadlineExpired");
        Messages.Message(worldMapStatus, MessageTypeDefOf.NegativeEvent, historical: false);
        StartConfirmSettledRaidCleanupSnapshot(session.EventId);
    }

    private static TimeSpan RaidSettlementHttpTimeout(ActiveRaidBattleSession session)
    {
        TimeSpan remaining = session.FinalRemaining;
        if (remaining <= TimeSpan.FromSeconds(5))
        {
            return TimeSpan.FromSeconds(5);
        }

        return remaining < TimeSpan.FromSeconds(60)
            ? remaining
            : TimeSpan.FromSeconds(60);
    }
}
