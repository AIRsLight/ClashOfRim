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
        LongEventHandler.QueueLongEvent(
            action,
            textKey,
            doAsynchronously: false,
            exceptionHandler: ex => Log.Warning("[ClashOfRim] Remote map long event failed: " + ex),
            showExtraUIInfo: false);
    }

    private static void SetRemoteMapLongEventText(string textKey)
    {
        LongEventHandler.SetCurrentEventText(ClashOfRimText.Key(textKey));
    }

    private static void ShowRemoteMapLoadingProgress(string status)
    {
        ShowServerEntryProgressWindow(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"),
            status,
            -1f,
            canClose: false);
    }

    private void ReportRemoteMapDownloadFailureFromWorker(ClientSynchronizationCoordinator.Lease operation, string message)
    {
        operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, _ =>
        {
            worldMapStatus = message;
            CloseServerEntryProgressWindowNow();
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
        });
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
        StartCreateRaid(new ClientRaidAttackForce(new CaravanRaidAttackForceSource(caravan)), target);
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
        StartCreateRaid(new ClientRaidAttackForce(new TransporterRaidAttackForceSource(
            transporters, target.Tile, transporterKey, target.TileLayerId)), target);
    }

    private void StartCreateRaid(ClientRaidAttackForce attackForce, ModWorldMapMarkerDto target)
    {
        if (!pvpEnabled)
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            worldMapStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            worldMapStatus = failureReason;
            return;
        }

        if (string.IsNullOrWhiteSpace(target.OwnerUserId)
            || string.IsNullOrWhiteSpace(target.OwnerColonyId)
            || string.IsNullOrWhiteSpace(target.MapId)
            || string.IsNullOrWhiteSpace(target.WorldObjectId))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusTargetIncomplete");
            return;
        }

        var context = sessionLifecycle.ReadContext();
        target = ClientProtocolJson.Deserialize<ModWorldMapMarkerDto>(ClientProtocolJson.Serialize(target), type => type);
        if (!attackForce.TryCapture(context, target, out var force, out failureReason))
        {
            worldMapStatus = failureReason;
            return;
        }
        string idempotencyKey = $"raid-start:{context.UserId}:{context.CurrentSnapshotId ?? string.Empty}:{force!.SourceLoadId}:{target.OwnerUserId}:{target.OwnerColonyId}:{DateTime.UtcNow.Ticks}";
        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason))
        {
            worldMapStatus = failureReason;
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusBattleDownloading",
            FormatSupportTargetLabel(target).Named("TARGET"));
        ShowRemoteMapLoadingProgress(worldMapStatus);
        manualOperation.RunContinuation(async operation =>
        {
            try
            {
                var command = new ClientRaidPreparationCommand(idempotencyKey, new ClientRaidTarget(
                    target.OwnerUserId, target.OwnerColonyId ?? string.Empty, target.WorldObjectId, target.MapId ?? string.Empty, target.Tile));
                ClientRaidPreparationResult result = await ClientSessionWork.Observe(
                    raidPreparation.PrepareAsync(context, command, ClientSessionWork.Token));
                using var publication = ClientSessionWork.EnterPublication();
                if (result.Prepared is not { } prepared)
                {
                    ReportRemoteMapDownloadFailureFromWorker(operation, ClientRaidPreparationPresentation.Failure(result.Failure!));
                    return;
                }
                operation.QueueContinuation(EnqueueClashOfRimMainThreadAction, next =>
                    FinishEnterRaidBattle(idempotencyKey, prepared.EventId, prepared.PreparationId, target,
                        context.UserId, context.ColonyId, context.CurrentSnapshotId ?? string.Empty,
                        prepared.Snapshot, attackForce, force,
                        prepared.BattleDuration, prepared.SettlementGraceDuration, prepared.Guards, next));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                ReportRemoteMapDownloadFailureFromWorker(operation, ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusCreateException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE")));
                Log.Warning("[ClashOfRim] Raid creation failed: " + ex);
            }
        });
    }

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
        => observationMaps.OpenAsync(target, mode);

    private void FinishEnterRaidBattle(
        string raidIdempotencyKey,
        string raidEventId,
        string raidPreparationId,
        ModWorldMapMarkerDto target,
        string attackerUserId,
        string attackerColonyId,
        string attackerSnapshotId,
        RemoteSnapshotOpenPayload openPayload,
        ClientRaidAttackForce attackForce,
        ClientRaidForceManifest force,
        TimeSpan battleDuration,
        TimeSpan settlementGraceDuration,
        ModRaidGuardDeploymentDto? guardDeployment,
        ClientSynchronizationCoordinator.Lease operation)
    {
        string sessionId = $"raidbattle:{attackerUserId}:{raidEventId}:{DateTime.UtcNow.Ticks}";
        var openRequest = new RemoteSessionMapOpenRequest
        {
            Target = target,
            Mode = RemoteSessionMapParent.RaidBattleMode,
            SessionId = sessionId,
            SnapshotId = openPayload.SnapshotId,
            RelatedEventId = raidEventId,
            Package = openPayload.Package,
            Payload = openPayload.Payload,
            CloseExistingObservationSessions = true,
            FailureCleanupReason = "raid battle map generation failure"
        };
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.RemoteSessionMap.StatusGenerating",
            FormatSupportTargetLabel(target).Named("TARGET"));
        ShowServerEntryProgressWindowNow(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"),
            worldMapStatus,
            -1f,
            canClose: false);
        operation.QueueContinuation(
            (run, _) => QueueRemoteMapLongEvent("ClashOfRim.RemoteSessionMap.StatusGeneratingLong", run),
            next => OpenPreparedRaidBattle(
                raidIdempotencyKey,
                raidEventId,
                raidPreparationId,
                target,
                attackerUserId,
                attackerColonyId,
                attackerSnapshotId,
                openRequest,
                openPayload.Package.NextLineageToken ?? string.Empty,
                attackForce,
                force,
                battleDuration,
                settlementGraceDuration,
                guardDeployment,
                next));
    }

    private void OpenPreparedRaidBattle(
        string raidIdempotencyKey,
        string raidEventId,
        string raidPreparationId,
        ModWorldMapMarkerDto target,
        string attackerUserId,
        string attackerColonyId,
        string attackerSnapshotId,
        RemoteSessionMapOpenRequest openRequest,
        string defenderLineageToken,
        ClientRaidAttackForce attackForce,
        ClientRaidForceManifest force,
        TimeSpan battleDuration,
        TimeSpan settlementGraceDuration,
        ModRaidGuardDeploymentDto? guardDeployment,
        ClientSynchronizationCoordinator.Lease continuation)
    {
        LocalMutationKind operation = LocalMutationKind.Raid;
        bool atomicStarted = false;
        bool submitted = false;
        try
        {
            if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = blockedMessage;
                Log.Warning("[ClashOfRim][Raid] Refusing to open prepared raid battle while another local atomic mutation is pending: event="
                    + raidEventId
                    + ", target="
                    + (target.WorldObjectId ?? string.Empty)
                    + ", blocked="
                    + blockedMessage
                    + ".");
                Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            if (attackForce.EntryFailure() is { } entryFailure)
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = entryFailure;
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshot"));
            atomicStarted = true;
            ClashLog.Message("[ClashOfRim][Raid] Opening prepared raid battle under local atomic mutation: event="
                + raidEventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ", attackerSnapshot="
                + attackerSnapshotId
                + ".");

            if (!RemoteSessionMapUtility.TryOpen(openRequest, out RemoteSessionMapOpenResult openResult))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = openResult.FailureReason;
                Log.Warning("[ClashOfRim][Raid] Prepared raid battle map open failed before event creation: event="
                    + raidEventId
                    + ", reason="
                    + worldMapStatus);
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            ActiveRaidBattleSession session = RemoteMapSessionController.CreateRaidBattleSession(
                raidEventId,
                attackerUserId,
                attackerColonyId,
                attackerSnapshotId,
                openRequest,
                openResult,
                defenderLineageToken,
                battleDuration,
                settlementGraceDuration,
                guardDeployment);

            if (!attackForce.TryEnterBattle(session, openResult.Map, out string entryFailureReason))
            {
                CloseServerEntryProgressWindowNow();
                worldMapStatus = entryFailureReason;
                Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                RemoteMapSessionController.CloseRaidBattleForExternalResolution(
                    session.EventId,
                    "raid attack pawn transfer failed");
                return;
            }

            CloseServerEntryProgressWindowNow();
            ClashLog.Message("[ClashOfRim][Raid] Prepared raid battle map opened and attackers moved; submitting authoritative start snapshot: event="
                + session.EventId
                + ", clientMap="
                + session.ClientMapId
                + ", attackerPawns="
                + session.AttackPawnThingIds.Count
                + ".");
            submitted = true;
            StartSubmitPreparedRaid(
                raidIdempotencyKey,
                raidPreparationId,
                session,
                target,
                force.PawnGlobalKeys,
                force.CarriedThings,
                continuation);
        }
        catch (Exception ex)
        {
            CloseServerEntryProgressWindowNow();
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.StatusGenerateException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            Log.Warning("[ClashOfRim] Prepared raid battle map open failed: " + ex);
        }
        finally
        {
            if (!submitted)
            {
                if (atomicStarted)
                {
                    ClearLocalAtomicMutation();
                }
            }
        }
    }

    private void StartSubmitPreparedRaid(
        string idempotencyKey,
        string raidPreparationId,
        ActiveRaidBattleSession session,
        ModWorldMapMarkerDto target,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Raid;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            worldMapStatus = blockedMessage;
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot submission blocked after local battle map mutation: event="
                + session.EventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ", blocked="
                + blockedMessage
                + ". The local battle map cannot continue without server confirmation.");
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        RemoteSessionMapParent? activeBattleParent = RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(session));
        Map? activeBattleMap = activeBattleParent?.Map;
        if (activeBattleMap is null)
        {
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusMissingPreparedBattleMap",
                session.EventId.Named("EVENTID"));
            Log.Warning("[ClashOfRim][Raid] Refusing to submit prepared raid "
                + session.EventId
                + " because the local save has no active battle map. This path must not recreate a remote map.");
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot submission could not acquire upload transaction after local battle map mutation: event="
                + session.EventId
                + ", target="
                + (target.WorldObjectId ?? string.Empty)
                + ".");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusPrepareSnapshot"));
        SetRemoteMapLongEventText("ClashOfRim.Raid.StatusPrepareSnapshotLong");
        ClashLog.Message("[ClashOfRim][Raid] Packaging prepared raid snapshot: event="
            + session.EventId
            + ", runtimeMap=Map_"
            + activeBattleMap.uniqueID
            + ", sourceMap="
            + session.TargetMapId
            + ", clientMap="
            + session.ClientMapId
            + ", attackerPawns="
            + session.AttackPawnThingIds.Count
            + ".");
        if (!snapshotPreparation.TryBuild(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            worldMapStatus = ClashOfRimText.Key(
                "ClashOfRim.Raid.StatusAttackerSnapshotException",
                "SaveToMemoryFailed".Named("TYPE"),
                buildFailureReason.Named("MESSAGE"));
            uploadTransaction.Dispose();
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaid(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings));
            return;
        }

        StartSubmitPreparedRaidWithSnapshot(
            idempotencyKey,
            raidPreparationId,
            session,
            target,
            pawnGlobalKeys,
            carriedThings,
            build.Package!,
            build.Payload!,
            continuation: uploadTransaction);
    }

    private void StartSubmitPreparedRaidWithSnapshot(
        string idempotencyKey,
        string raidPreparationId,
        ActiveRaidBattleSession session,
        ModWorldMapMarkerDto target,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ModThingReferenceDto> carriedThings,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Raid;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            continuation?.Dispose();
            worldMapStatus = blockedMessage;
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot multipart submission blocked by another local atomic mutation: event="
                + session.EventId
                + ", blocked="
                + blockedMessage
                + ".");
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaidWithSnapshot(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings,
                    confirmedSnapshot,
                    confirmedPayload,
                    continuation: null));
            return;
        }

        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            worldMapStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Log.Warning("[ClashOfRim][Raid] Prepared raid start snapshot multipart submission could not acquire upload transaction: event="
                + session.EventId
                + ".");
            Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
            ShowUnconfirmedSnapshotFailure(
                operation,
                worldMapStatus,
                () => StartSubmitPreparedRaidWithSnapshot(
                    idempotencyKey,
                    raidPreparationId,
                    session,
                    target,
                    pawnGlobalKeys,
                    carriedThings,
                    confirmedSnapshot,
                    confirmedPayload,
                    continuation: null));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Raid.StatusSubmitting", FormatSupportTargetLabel(target).Named("TARGET")));
        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusSubmitting",
            FormatSupportTargetLabel(target).Named("TARGET"));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> result =
                    await ClientSessionWork.Observe(client.CreateRaidWithSnapshotAsync(
                        idempotencyKey,
                        raidPreparationId,
                        target.OwnerUserId ?? string.Empty,
                        target.OwnerColonyId ?? string.Empty,
                        session.DefenderSnapshotId,
                        target.WorldObjectId ?? string.Empty,
                        target.MapId ?? string.Empty,
                        target.Tile,
                        isHostile: true,
                        defenderOnline: false,
                        defenderWealth: 1_000_000,
                        defenderRaidCooldownUntilUtc: null,
                        pawnGlobalKeys: pawnGlobalKeys,
                        carriedThings: carriedThings,
                        confirmedSnapshot,
                        confirmedPayload,
                        session.GuardDeploymentId));

                using var publication = ClientSessionWork.EnterPublication();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!result.Success || result.Response is null)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusCreateFailed",
                            result.ErrorCode.Named("CODE"),
                            result.Message.Named("MESSAGE"));
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            worldMapStatus,
                            () => StartSubmitPreparedRaidWithSnapshot(
                                idempotencyKey,
                                raidPreparationId,
                                session,
                                target,
                                pawnGlobalKeys,
                                carriedThings,
                                confirmedSnapshot,
                                confirmedPayload));
                        return;
                    }

                    ModProtocolResponseDto? response = result.Response.Result;
                    if (response is not null && !response.Accepted)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusCreateRejected",
                            response.ErrorCode.Named("CODE"),
                            response.Message.Named("MESSAGE"));
                        Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            worldMapStatus,
                            () => StartSubmitPreparedRaidWithSnapshot(
                                idempotencyKey,
                                raidPreparationId,
                                session,
                                target,
                                pawnGlobalKeys,
                                carriedThings,
                                confirmedSnapshot,
                                confirmedPayload));
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        session.AttackerSnapshotId = result.Response.AppliedSnapshotId!;
                        snapshotPublication.PublishCurrentGame(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }
                    ApplyRaidBattleServerDeadlines(session, result.Response);

                    ClashLog.Message("[ClashOfRim][Raid] Prepared raid start snapshot accepted: event="
                        + result.Response.EventId
                        + ", appliedSnapshot="
                        + (result.Response.AppliedSnapshotId ?? string.Empty)
                        + ", nextLineage="
                        + (result.Response.NextLineageToken ?? string.Empty)
                        + ".");

                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreated",
                        result.Response.EventId.Named("EVENTID"));
                    Messages.Message(
                        ClashOfRimText.Key("ClashOfRim.Raid.CreatedMessage", FormatSupportTargetLabel(target).Named("TARGET")),
                        MessageTypeDefOf.ThreatBig,
                        historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    worldMapStatus = ClashOfRimText.Key(
                        "ClashOfRim.Raid.StatusCreateException",
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(worldMapStatus, MessageTypeDefOf.RejectInput, historical: false);
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        worldMapStatus,
                        () => StartSubmitPreparedRaidWithSnapshot(
                            idempotencyKey,
                            raidPreparationId,
                            session,
                            target,
                            pawnGlobalKeys,
                            carriedThings,
                            confirmedSnapshot,
                            confirmedPayload));
                });
                Log.Warning("[ClashOfRim] Prepared raid submission failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    private static void ApplyRaidBattleServerDeadlines(
        ActiveRaidBattleSession session,
        ModEventCreationResponseDto response)
    {
        if (!TryParseServerUtc(response.RaidStartedAtUtc, out DateTimeOffset startedAtUtc)
            || !TryParseServerUtc(response.RaidDeadlineUtc, out DateTimeOffset deadlineUtc)
            || !TryParseServerUtc(response.RaidFinalDeadlineUtc, out DateTimeOffset finalDeadlineUtc))
        {
            ClashLog.Message("[ClashOfRim][Raid] Raid creation response did not include server deadlines; keeping local fallback deadlines: event="
                + session.EventId
                + ".");
            return;
        }

        session.StartedAtUtcTicks = startedAtUtc.UtcDateTime.Ticks;
        session.DeadlineUtcTicks = deadlineUtc.UtcDateTime.Ticks;
        session.FinalDeadlineUtcTicks = finalDeadlineUtc.UtcDateTime.Ticks;
        ClashLog.Message("[ClashOfRim][Raid] Applied server raid deadlines: event="
            + session.EventId
            + ", deadline="
            + deadlineUtc.ToString("O")
            + ", finalDeadline="
            + finalDeadlineUtc.ToString("O")
            + ".");
    }

    private void StartConfirmRaidStartSnapshot(ActiveRaidBattleSession session)
    {
        StartConfirmLocalMutationSnapshot(new ClientLocalSnapshotRequest
        {
            Operation = LocalMutationKind.Raid,
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Raid.StatusAttackerSnapshotUploading"),
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
                ex.Message.Named("MESSAGE"))
        });
    }

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

        if (TryReportRaidSettlementSupportLossesBeforeSnapshot(context))
        {
            return;
        }

        QueueRemoteMapLongEvent(
            "ClashOfRim.Raid.StatusSettlementSavingLong",
            () => ContinueFinishActiveRaidBattleAfterSupportResolution(context));
    }

    private void ContinueFinishActiveRaidBattleAfterSupportResolution(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
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

    private bool TryReportRaidSettlementSupportLossesBeforeSnapshot(RaidBattleSettlementContext context)
    {
        ActiveRaidBattleSession session = context.Session;
        ClashOfRimGameComponent? component = Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();
        if (component is null)
        {
            return false;
        }

        List<PendingSupportPawnLoss> pending = new();
        foreach (ActiveSupportPawnAssignment assignment in component.CopyActiveSupportAssignments())
        {
            if (!assignment.AutoReturnOnSettlement || assignment.FinishInProgress)
            {
                continue;
            }

            Pawn? pawn = SupportPawnLookup.FindActive(assignment.PawnThingId);
            if (pawn is null || pawn.Dead)
            {
                continue;
            }

            if (pawn.Map is null || !MapIdsMatch("Map_" + pawn.Map.uniqueID, session.ClientMapId))
            {
                continue;
            }

            pending.Add(new PendingSupportPawnLoss(
                assignment,
                pawn,
                string.IsNullOrWhiteSpace(pawn.LabelShort) ? assignment.PawnLabel : pawn.LabelShort));
        }

        if (pending.Count == 0)
        {
            return false;
        }

        foreach (PendingSupportPawnLoss loss in pending)
        {
            component.MarkSupportAssignmentInProgress(loss.Assignment.EventId, inProgress: true);
        }

        worldMapStatus = ClashOfRimText.Key(
            "ClashOfRim.Raid.StatusSettlementReportingSupportLoss",
            pending.Count.Named("COUNT"));

        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                foreach (PendingSupportPawnLoss loss in pending)
                {
                    string idempotencyKey =
                        $"support-finish:{settings.UserId}:{settings.ColonyId}:{loss.Assignment.EventId}:RaidSettlementLost";
                    ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> result =
                        await ClientSessionWork.Observe(client.FinishSupportPawnAsync(
                            idempotencyKey,
                            loss.Assignment.EventId,
                            "RaidSettlementLost",
                            loss.Assignment.PawnGlobalKey,
                            loss.PawnLabel,
                            pawnDead: true,
                            pawnPackage: null));

                    using var branchPublication = ClientSessionWork.EnterPublication();
                    if (!result.Success || result.Response is null)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusSettlementSupportLossFailed",
                            loss.PawnLabel.Named("PAWN"),
                            result.ErrorCode.Named("CODE"),
                            result.Message.Named("MESSAGE"));
                        ShowRaidSettlementSupportLossFailure(context, component, pending);
                        return;
                    }

                    ModProtocolResponseDto? response = result.Response.Result;
                    if (response is not null && !response.Accepted)
                    {
                        worldMapStatus = ClashOfRimText.Key(
                            "ClashOfRim.Raid.StatusSettlementSupportLossRejected",
                            loss.PawnLabel.Named("PAWN"),
                            response.ErrorCode.Named("CODE"),
                            response.Message.Named("MESSAGE"));
                        ShowRaidSettlementSupportLossFailure(context, component, pending);
                        return;
                    }
                }

                using var publication = ClientSessionWork.EnterPublication();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    foreach (PendingSupportPawnLoss loss in pending)
                    {
                        Pawn? currentPawn = SupportPawnLookup.FindActive(loss.Assignment.PawnThingId) ?? loss.Pawn;
                        if (currentPawn is not null && !currentPawn.Destroyed && !PawnExchangeLifecycleService.RemoveFromLocalWorld(currentPawn))
                        {
                            worldMapStatus = ClashOfRimText.Key(
                                "ClashOfRim.Raid.StatusSettlementSupportLossLocalRemoveFailed",
                                loss.PawnLabel.Named("PAWN"));
                            ShowRaidSettlementSupportLossFailure(context, component, pending);
                            return;
                        }

                        component.MarkSupportAssignmentFinished(loss.Assignment.EventId);
                    }

                    QueueRemoteMapLongEvent(
                        "ClashOfRim.Raid.StatusSettlementSavingLong",
                        () => ContinueFinishActiveRaidBattleAfterSupportResolution(context));
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                worldMapStatus = ClashOfRimText.Key(
                    "ClashOfRim.Raid.StatusSettlementSupportLossException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                ShowRaidSettlementSupportLossFailure(context, component, pending);
                Log.Warning("[ClashOfRim] Raid settlement support loss reporting failed: " + ex);
            }
        });

        return true;
    }

    private void ShowRaidSettlementSupportLossFailure(
        RaidBattleSettlementContext context,
        ClashOfRimGameComponent component,
        IReadOnlyList<PendingSupportPawnLoss> pending)
    {
        ActiveRaidBattleSession session = context.Session;
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            if (!string.Equals(
                    ClashOfRimGameComponent.ActiveRaidBattleSession?.EventId,
                    session.EventId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (session.IsFinalDeadlineExpired)
            {
                HandleRaidBattleFinalDeadlineExpired(session);
                return;
            }

            foreach (PendingSupportPawnLoss loss in pending)
            {
                component.MarkSupportAssignmentInProgress(loss.Assignment.EventId, inProgress: false);
            }

            RemoteMapSessionController.ResetRaidBattleFinish(session);
            ShowUnconfirmedSnapshotFailure(
                LocalMutationKind.Raid,
                worldMapStatus,
                () => StartFinishActiveRaidBattle(session, context.FinishReason));
        });
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
    private sealed class PendingSupportPawnLoss
    {
        public PendingSupportPawnLoss(
            ActiveSupportPawnAssignment assignment,
            Pawn pawn,
            string pawnLabel)
        {
            Assignment = assignment;
            Pawn = pawn;
            PawnLabel = pawnLabel;
        }

        public ActiveSupportPawnAssignment Assignment { get; }
        public Pawn Pawn { get; }
        public string PawnLabel { get; }
    }
}
