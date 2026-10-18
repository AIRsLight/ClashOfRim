using AIRsLight.ClashOfRim.Modules.Snapshots;
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
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenCaravanSupportPawnMenu(
        Caravan caravan,
        IReadOnlyList<ModWorldMapMarkerDto> targets,
        IReadOnlyList<Pawn> pawns)
    {
        if (targets.Count == 0)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusNoTargets");
            return;
        }

        List<Pawn> ownerPawns = pawns
            .Where(pawn => pawn != null && !pawn.Dead && caravan.IsOwner(pawn))
            .ToList();
        if (ownerPawns.Count == 0)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusNoPawns");
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (ModWorldMapMarkerDto target in targets)
        {
            ModWorldMapMarkerDto capturedTarget = target;
            foreach (Pawn pawn in ownerPawns)
            {
                Pawn capturedPawn = pawn;
                string label = ClashOfRimText.Key(
                    "ClashOfRim.Support.CaravanPawnOption",
                    capturedPawn.LabelShort.Named("PAWN"),
                    FormatSupportTargetLabel(capturedTarget).Named("TARGET"));
                options.Add(new FloatMenuOption(label, () =>
                    {
                        string summary = ClashOfRimText.Key(
                            "ClashOfRim.Support.ConfirmSend",
                            capturedPawn.LabelShort.Named("PAWN"),
                            FormatSupportTargetLabel(capturedTarget).Named("TARGET"));
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            summary,
                            () => OpenSupportDurationDialog(caravan, capturedPawn, capturedTarget)));
                    }));
            }
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void OpenSupportDurationDialog(Caravan caravan, Pawn pawn, ModWorldMapMarkerDto target)
    {
        Find.WindowStack.Add(new SupportDurationDialogWindow(
            pawn.LabelShortCap,
            FormatSupportTargetLabel(target),
            (supportDurationDays, permanentSupport) =>
                StartCreateSupportPawnFromCaravan(caravan, pawn, target, supportDurationDays, permanentSupport)));
    }

    private void StartCreateSupportPawnFromCaravan(
        Caravan caravan,
        Pawn pawn,
        ModWorldMapMarkerDto target,
        int? supportDurationDays,
        bool permanentSupport)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            tradeStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        if (!caravan.ContainsPawn(pawn) || !caravan.IsOwner(pawn))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusPawnNotInCaravan");
            return;
        }

        if (target.Tile != caravan.Tile)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusTargetTileMismatch");
            return;
        }

        string pawnGlobalKey = PawnGlobalIdUtility.Build(settings.UserId, pawn);
        ModPawnExchangePackageDto package = new PawnExchangeCapture(settings.UserId, settings.ColonyId, settings.CurrentSnapshotId)
            .Capture(pawn, pawnGlobalKey);
        ModCrossMapPawnReferenceDto reference = package.Reference!;
        if (package.Scribe is null || string.IsNullOrWhiteSpace(package.Scribe.Xml))
        {
            tradeStatus = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusMissingScribe",
                ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelSupport").Named("LABEL"));
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        bool autoReturnOnSettlement = string.Equals(target.Kind, "ActiveRaidTarget", StringComparison.Ordinal);
        var targetContext = new ModEventTargetContextDto
        {
            WorldObjectId = target.WorldObjectId,
            MapUniqueId = target.MapId,
            Tile = target.Tile,
            LandingMode = "MapEdge"
        };
        string idempotencyKey = $"support-pawn:{settings.UserId}:{settings.CurrentSnapshotId}:{pawn.ThingID}:{target.OwnerUserId}:{target.OwnerColonyId}:{DateTime.UtcNow.Ticks}";
        BeginLocalAtomicMutation(
            LocalMutationKind.SupportSend,
            ClashOfRimText.Key("ClashOfRim.Support.StatusReserving"));
        caravan.RemovePawn(pawn);
        DestroyEmptySourceCaravan(caravan);
        if (!permanentSupport
            && !SupportPawnWorldPawnContextUtility.MarkDepartingTemporarySupportPawn(
                pawn,
                pawnGlobalKey,
                out string contextFailure))
        {
            Log.Warning("[ClashOfRim][Support] Temporary support pawn was not preserved as world pawn context: "
                + contextFailure
                + ".");
        }

        StartSubmitRemovedSupportPawn(
            pawn,
            pawnGlobalKey,
            reference,
            package,
            target,
            targetContext,
            caravan.Tile,
            caravan.GetUniqueLoadID(),
            permanentSupport,
            supportDurationDays,
            autoReturnOnSettlement,
            idempotencyKey);
    }

    private static void DestroyEmptySourceCaravan(Caravan caravan)
    {
        if (caravan is null || caravan.Destroyed || caravan.PawnsListForReading?.Count > 0)
        {
            return;
        }

        caravan.Destroy();
        ClashLog.Message("[ClashOfRim][Support] Removed empty source caravan after sending its last pawn as support.");
    }

    private void StartSubmitRemovedSupportPawn(
        Pawn pawn,
        string pawnGlobalKey,
        ModCrossMapPawnReferenceDto reference,
        ModPawnExchangePackageDto package,
        ModWorldMapMarkerDto target,
        ModEventTargetContextDto targetContext,
        int sourceTile,
        string sourceCaravanLoadId,
        bool permanentSupport,
        int? supportDurationDays,
        bool autoReturnOnSettlement,
        string idempotencyKey)
    {
        LocalMutationKind operation = LocalMutationKind.SupportSend;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Support.StatusPackagingSnapshot"));
        if (!snapshotPreparation.TryBuild(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusSnapshotBuildFailed", buildFailureReason.Named("REASON"));
            ShowUnconfirmedSnapshotFailure(
                operation,
                tradeStatus,
                () => StartSubmitRemovedSupportPawn(
                    pawn,
                    pawnGlobalKey,
                    reference,
                    package,
                    target,
                    targetContext,
                    sourceTile,
                    sourceCaravanLoadId,
                    permanentSupport,
                    supportDurationDays,
                    autoReturnOnSettlement,
                    idempotencyKey));
            return;
        }

        StartSubmitRemovedSupportPawnWithSnapshot(
            pawn,
            pawnGlobalKey,
            reference,
            package,
            target,
            targetContext,
            sourceTile,
            sourceCaravanLoadId,
            permanentSupport,
            supportDurationDays,
            autoReturnOnSettlement,
            idempotencyKey,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitRemovedSupportPawnWithSnapshot(
        Pawn pawn,
        string pawnGlobalKey,
        ModCrossMapPawnReferenceDto reference,
        ModPawnExchangePackageDto package,
        ModWorldMapMarkerDto target,
        ModEventTargetContextDto targetContext,
        int sourceTile,
        string sourceCaravanLoadId,
        bool permanentSupport,
        int? supportDurationDays,
        bool autoReturnOnSettlement,
        string idempotencyKey,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        LocalMutationKind operation = LocalMutationKind.SupportSend;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Support.StatusSubmittingTransaction"));
        tradeStatus = ClashOfRimText.Key(
            "ClashOfRim.Support.StatusSubmitting",
            pawn.LabelShort.Named("PAWN"),
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
                    await ClientSessionWork.Observe(client.CreateSupportPawnWithSnapshotAsync(
                        idempotencyKey,
                        target.OwnerUserId,
                        target.OwnerColonyId ?? string.Empty,
                        null,
                        pawnGlobalKey,
                        pawn.LabelShort,
                        reference,
                        package,
                        targetContext,
                        sourceTile,
                        sourceCaravanLoadId,
                        permanentSupport,
                        permanentSupport ? null : supportDurationDays,
                        expiresAtGameTicks: null,
                        autoReturnOnSettlement,
                        confirmedSnapshot,
                        confirmedPayload));

                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusCreateFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        LocalMutationKind.SupportSend,
                        tradeStatus,
                        () => StartSubmitRemovedSupportPawn(
                            pawn,
                            pawnGlobalKey,
                            reference,
                            package,
                            target,
                            targetContext,
                            sourceTile,
                            sourceCaravanLoadId,
                            permanentSupport,
                            supportDurationDays,
                            autoReturnOnSettlement,
                            idempotencyKey));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.StatusCreateRejected",
                        response.ErrorCode.Named("CODE"),
                        response.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        LocalMutationKind.SupportSend,
                        tradeStatus,
                        () => StartSubmitRemovedSupportPawn(
                            pawn,
                            pawnGlobalKey,
                            reference,
                            package,
                            target,
                            targetContext,
                            sourceTile,
                            sourceCaravanLoadId,
                            permanentSupport,
                            supportDurationDays,
                            autoReturnOnSettlement,
                            idempotencyKey));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        snapshotPublication.PublishCurrentGame(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.StatusCreated",
                        pawn.LabelShort.Named("PAWN"),
                        result.Response.EventId.Named("EVENTID"));
                    Messages.Message(
                        ClashOfRimText.Key("ClashOfRim.Support.CreatedMessage", pawn.LabelShort.Named("PAWN")),
                        MessageTypeDefOf.PositiveEvent,
                        historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    LocalMutationKind.SupportSend,
                    tradeStatus,
                    () => StartSubmitRemovedSupportPawn(
                        pawn,
                        pawnGlobalKey,
                        reference,
                        package,
                        target,
                        targetContext,
                        sourceTile,
                        sourceCaravanLoadId,
                        permanentSupport,
                        supportDurationDays,
                        autoReturnOnSettlement,
                        idempotencyKey));
                Log.Warning("[ClashOfRim] Support pawn creation failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    internal bool TryFinishSupportAssignmentFromQuest(string eventId)
    {
        return TryStartSupportAssignmentReturnFromQuest(eventId, "QuestExpired");
    }

    internal bool TryStartSupportAssignmentReturnFromQuest(string eventId, string finishReason)
    {
        if (string.IsNullOrWhiteSpace(eventId)
            || !settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return false;
        }

        ClashOfRimGameComponent? component = Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();
        ActiveSupportPawnAssignment? assignment = component?.FindSupportAssignmentByEventId(eventId);
        if (component is null || assignment is null || assignment.FinishInProgress || assignment.PermanentSupport)
        {
            return false;
        }

        Pawn? pawn = FindSupportPawnByThingId(assignment.PawnThingId);
        if (pawn is null)
        {
            return false;
        }

        return StartSupportPawnDeparture(component, assignment, pawn, finishReason);
    }

    internal bool TryConfirmDepartedSupportAssignment(string eventId, string finishReason)
    {
        if (string.IsNullOrWhiteSpace(eventId)
            || !settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return false;
        }

        ClashOfRimGameComponent? component = Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();
        ActiveSupportPawnAssignment? assignment = component?.FindSupportAssignmentByEventId(eventId);
        if (component is null || assignment is null || !assignment.FinishInProgress || assignment.PermanentSupport)
        {
            return false;
        }

        Pawn? pawn = FindSupportPawnByThingId(assignment.PawnThingId);
        if (pawn is null || (!pawn.Dead && pawn.Spawned))
        {
            return false;
        }

        StartFinishDepartedSupportPawn(component, assignment, pawn, finishReason);
        return true;
    }

    private bool StartSupportPawnDeparture(
        ClashOfRimGameComponent component,
        ActiveSupportPawnAssignment assignment,
        Pawn pawn,
        string finishReason)
    {
        if (component is null || assignment is null || pawn is null)
        {
            return false;
        }

        if (pawn.Dead)
        {
            StartFinishDepartedSupportPawn(component, assignment, pawn, finishReason);
            return true;
        }

        component.MarkSupportAssignmentInProgress(assignment.EventId, inProgress: true);
        PawnExchangeLifecycleService.StripExchangeTags(pawn);

        Faction? originalFaction = SupportPawnFactionUtility.ResolveOriginalFaction(assignment);
        PrepareSupportPawnForReturn(pawn, originalFaction);

        if (pawn.drafter is not null)
        {
            pawn.drafter.Drafted = false;
        }

        if (!pawn.Spawned || pawn.MapHeld is null)
        {
            StartFinishDepartedSupportPawn(component, assignment, pawn, finishReason);
            return true;
        }

        LordMaker.MakeNewLord(
            pawn.Faction,
            new SupportPawnReturnLordJob(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true),
            pawn.MapHeld,
            Gen.YieldSingle(pawn));
        pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusReturnDeparting", pawn.LabelShort.Named("PAWN"));
        Messages.Message(tradeStatus, pawn, MessageTypeDefOf.NeutralEvent, historical: false);
        return true;
    }

    private static void PrepareSupportPawnForReturn(Pawn pawn, Faction? originalFaction)
    {
        Caravan? caravan = pawn.GetCaravan();
        if (caravan is not null)
        {
            CaravanInventoryUtility.MoveAllInventoryToSomeoneElse(pawn, caravan.PawnsListForReading, null);
            caravan.RemovePawn(pawn);
        }

        if (originalFaction is not null && pawn.Faction != originalFaction)
        {
            pawn.SetFaction(originalFaction);
        }

        foreach (Pawn candidate in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
        {
            if (candidate.playerSettings?.Master == pawn)
            {
                candidate.playerSettings.Master = null;
            }
        }

        if (pawn.guest is not null)
        {
            if (pawn.InBed()
                && pawn.CurrentBed().Faction == Faction.OfPlayer
                && (pawn.Faction is null || !pawn.Faction.HostileTo(Faction.OfPlayer)))
            {
                pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Guest);
            }
            else
            {
                pawn.guest.SetGuestStatus(null, GuestStatus.Guest);
            }
        }

        if (pawn.carryTracker?.CarriedThing is not null && pawn.Spawned)
        {
            pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _, null);
        }

        Lord? currentLord = pawn.GetLord();
        currentLord?.Notify_PawnLost(pawn, PawnLostCondition.ForcedByQuest, null);

        if (!pawn.Awake())
        {
            RestUtility.WakeUp(pawn, true);
        }
    }

    private void StartFinishDepartedSupportPawn(
        ClashOfRimGameComponent component,
        ActiveSupportPawnAssignment assignment,
        Pawn pawn,
        string finishReason)
    {
        if (component is null || assignment is null || pawn is null)
        {
            return;
        }

        ModPawnExchangePackageDto? package = pawn.Dead
            ? null
            : new PawnExchangeCapture(settings.UserId, settings.ColonyId, settings.CurrentSnapshotId)
                .Capture(pawn, assignment.PawnGlobalKey, preservedMetadata: assignment.PawnReferenceMetadata);
        string pawnLabel = pawn.LabelShort;
        if (!RemoveSupportPawnFromLocalMap(pawn))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusFinishLocalRemoveFailed", pawnLabel.Named("PAWN"));
            component.MarkSupportAssignmentInProgress(assignment.EventId, inProgress: false);
            return;
        }

        component.MarkSupportAssignmentFinished(assignment.EventId);
        ClashSupportPawnQuestUtility.CompleteSupportQuest(assignment.EventId);

        StartConfirmLocalMutationSnapshot(new ClientLocalSnapshotRequest
        {
            Operation = LocalMutationKind.Support,
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusReturnSnapshotUploading", pawnLabel.Named("PAWN")),
            SetStatus = message => tradeStatus = message,
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Support.StatusReturnSnapshotConfirmed",
                pawnLabel.Named("PAWN"),
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            OnSuccessOnMainThread = _ => StartSubmitFinishedSupportPawn(assignment, pawnLabel, finishReason, package)
        });
    }

    private void StartSubmitFinishedSupportPawn(
        ActiveSupportPawnAssignment assignment,
        string pawnLabel,
        string finishReason,
        ModPawnExchangePackageDto? package)
    {
        string idempotencyKey = $"support-finish:{settings.UserId}:{settings.ColonyId}:{assignment.EventId}:{finishReason}";

        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> result =
                    await ClientSessionWork.Observe(client.FinishSupportPawnAsync(
                        idempotencyKey,
                        assignment.EventId,
                        finishReason,
                        assignment.PawnGlobalKey,
                        pawnLabel,
                        pawnDead: package is null,
                        package));

                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.StatusFinishFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.StatusFinishRejected",
                        response.ErrorCode.Named("CODE"),
                        response.Message.Named("MESSAGE"));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Support.StatusFinished",
                        pawnLabel.Named("PAWN"),
                        (result.Response.ReturnEventId ?? result.Response.NotificationEventId ?? string.Empty).Named("EVENTID"));
                    Messages.Message(
                        ClashOfRimText.Key("ClashOfRim.Support.FinishedMessage", pawnLabel.Named("PAWN")),
                        MessageTypeDefOf.PositiveEvent,
                        historical: false);
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                tradeStatus = ClashOfRimText.Key(
                    "ClashOfRim.Support.StatusFinishException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Support pawn finish failed: " + ex);
            }
        });
    }

    private static Pawn? FindActiveSupportPawn(ActiveSupportPawnAssignment assignment)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.PawnThingId))
        {
            return null;
        }

        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            Pawn? pawn = map.mapPawns?.AllPawnsSpawned
                .FirstOrDefault(candidate => string.Equals(candidate.ThingID, assignment.PawnThingId, StringComparison.Ordinal));
            if (pawn is not null)
            {
                return pawn;
            }
        }

        return null;
    }

    private static Pawn? FindSupportPawnByThingId(string thingId)
    {
        if (string.IsNullOrWhiteSpace(thingId))
        {
            return null;
        }

        Pawn? liveOrWorld = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
            .FirstOrDefault(candidate => string.Equals(candidate.ThingID, thingId, StringComparison.Ordinal));
        if (liveOrWorld is not null)
        {
            return liveOrWorld;
        }

        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            Pawn? pawn = map.mapPawns?.AllPawnsSpawned
                .FirstOrDefault(candidate => string.Equals(candidate.ThingID, thingId, StringComparison.Ordinal));
            if (pawn is not null)
            {
                return pawn;
            }
        }

        return null;
    }

    private static bool RemoveSupportPawnFromLocalMap(Pawn pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            return true;
        }

        return PawnExchangeLifecycleService.RemoveFromLocalWorld(pawn);
    }

}
