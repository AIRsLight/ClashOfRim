using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
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

        string pawnGlobalKey = BuildPawnGlobalKey(pawn, caravan);
        ModCrossMapPawnReferenceDto reference = BuildPawnReference(pawn, pawnGlobalKey);
        ModPawnExchangePackageDto package = BuildPawnExchangePackage(pawn, caravan, reference);
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
            ClashOfRimText.Key("ClashOfRim.Support.OperationSend"),
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
        string operation = ClashOfRimText.Key("ClashOfRim.Support.OperationSend");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Support.StatusPackagingSnapshot"));
        if (!TryBuildCurrentGameSnapshotPackage(
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
        string operation = ClashOfRimText.Key("ClashOfRim.Support.OperationSend");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
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
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> result =
                    await client.CreateSupportPawnWithSnapshotAsync(
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
                        confirmedPayload);

                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusCreateFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        ClashOfRimText.Key("ClashOfRim.Support.OperationSend"),
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
                        ClashOfRimText.Key("ClashOfRim.Support.OperationSend"),
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
                        PersistAcceptedSnapshotLineage(
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
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    ClashOfRimText.Key("ClashOfRim.Support.OperationSend"),
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
                EndSnapshotUploadTransaction();
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
        if (pawn is null || pawn.Dead)
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

        component.MarkSupportAssignmentInProgress(assignment.EventId, inProgress: true);
        if (!pawn.Dead)
        {
            PawnExchangeLifecycleService.StripExchangeTags(pawn);
        }

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
            : BuildPawnExchangePackageForSupportReturn(pawn, assignment);
        string pawnLabel = pawn.LabelShort;
        if (!RemoveSupportPawnFromLocalMap(pawn))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusFinishLocalRemoveFailed", pawnLabel.Named("PAWN"));
            component.MarkSupportAssignmentInProgress(assignment.EventId, inProgress: false);
            return;
        }

        component.MarkSupportAssignmentFinished(assignment.EventId);
        ClashSupportPawnQuestUtility.CompleteSupportQuest(assignment.EventId);

        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationSupport"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusReturnSnapshotUploading", pawnLabel.Named("PAWN")),
            RetryAction = () => StartConfirmFinishedSupportPawnSnapshot(assignment, pawnLabel, finishReason, package),
            SetStatus = message => tradeStatus = message,
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Support.StatusReturnSnapshotConfirmed",
                pawnLabel.Named("PAWN"),
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            OnSuccessOnMainThread = _ => StartSubmitFinishedSupportPawn(assignment, pawnLabel, finishReason, package)
        });
    }

    private void StartConfirmFinishedSupportPawnSnapshot(
        ActiveSupportPawnAssignment assignment,
        string pawnLabel,
        string finishReason,
        ModPawnExchangePackageDto? package)
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationSupport"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Support.StatusReturnSnapshotUploading", pawnLabel.Named("PAWN")),
            RetryAction = () => StartConfirmFinishedSupportPawnSnapshot(assignment, pawnLabel, finishReason, package),
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

        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> result =
                    await client.FinishSupportPawnAsync(
                        idempotencyKey,
                        assignment.EventId,
                        finishReason,
                        assignment.PawnGlobalKey,
                        pawnLabel,
                        pawnDead: package is null,
                        package);

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
            {
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

    private string BuildPawnGlobalKey(Pawn pawn, Caravan caravan)
    {
        return PawnGlobalIdUtility.Build(settings.UserId, pawn);
    }

    private string BuildRelatedPawnGlobalKey(Pawn pawn)
    {
        return PawnGlobalIdUtility.Build(settings.UserId, pawn);
    }

    private ModCrossMapPawnReferenceDto BuildPawnReference(
        Pawn pawn,
        string pawnGlobalKey,
        IReadOnlyDictionary<string, string?>? preservedMetadata = null)
    {
        Dictionary<string, string?> metadata = BuildPawnReferenceMetadata(pawn);
        if (preservedMetadata is not null)
        {
            foreach (KeyValuePair<string, string?> entry in preservedMetadata)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    metadata[entry.Key] = entry.Value;
                }
            }
        }

        return new ModCrossMapPawnReferenceDto
        {
            GlobalId = pawnGlobalKey,
            SourceSnapshotId = settings.CurrentSnapshotId,
            Name = pawn.LabelShort,
            Dead = pawn.Dead,
            Faction = pawn.Faction?.def?.defName,
            Metadata = metadata
        };
    }

    private Dictionary<string, string?> BuildPawnReferenceMetadata(Pawn pawn)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        ClashOfRimCompatibilityApi.CollectPawnReferenceMetadata(
            pawn,
            metadata,
            settings.UserId,
            settings.ColonyId);
        return metadata;
    }

    private ModPawnExchangePackageDto BuildPawnExchangePackage(
        Pawn pawn,
        Caravan caravan,
        ModCrossMapPawnReferenceDto reference)
    {
        var package = new ModPawnExchangePackageDto
        {
            PackageVersion = 1,
            Reference = reference,
            Identity = BuildPawnExchangeIdentity(pawn),
            Appearance = new ModPawnExchangeAppearanceDto
            {
                DisplayName = pawn.LabelShort,
                BodyTypeDef = pawn.story?.bodyType?.defName,
                HeadTypeDef = pawn.story?.headType?.defName,
                HairDef = pawn.story?.hairDef?.defName,
                BeardDef = pawn.style?.beardDef?.defName,
                SkinColor = null,
                HairColor = null
            },
            Status = new ModPawnExchangeStatusDto
            {
                Dead = pawn.Dead,
                BiologicalAgeTicks = pawn.ageTracker?.AgeBiologicalTicks,
                ChronologicalAgeTicks = pawn.ageTracker?.AgeChronologicalTicks,
                DeathCauseDef = null,
                HealthState = pawn.health?.State.ToString()
            },
            Apparel = pawn.apparel?.WornApparel
                .Select(apparel => ToPawnExchangeEquipmentItem(apparel, caravan, pawn, "apparel"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Equipment = pawn.equipment?.AllEquipmentListForReading
                .Select(equipment => ToPawnExchangeEquipmentItem(equipment, caravan, pawn, "equipment"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Relationships = BuildOneLayerRelationshipStubs(pawn),
            Scribe = BuildPawnScribePayload(
                pawn,
                referencedPawn => caravan.ContainsPawn(referencedPawn)
                    ? BuildPawnGlobalKey(referencedPawn, caravan)
                    : BuildRelatedPawnGlobalKey(referencedPawn))
        };
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, package);
        return package;
    }

    private static ModPawnExchangeIdentityDto BuildPawnExchangeIdentity(Pawn pawn)
    {
        var identity = new ModPawnExchangeIdentityDto
        {
            ThingDef = pawn.def?.defName,
            PawnKindDef = pawn.kindDef?.defName,
            FactionDef = pawn.Faction?.def?.defName,
            Gender = pawn.gender.ToString()
        };
        return identity;
    }

    private ModPawnExchangePackageDto BuildPawnExchangePackageForSupportReturn(
        Pawn pawn,
        ActiveSupportPawnAssignment assignment)
    {
        ModCrossMapPawnReferenceDto reference = BuildPawnReference(
            pawn,
            assignment.PawnGlobalKey,
            assignment.PawnReferenceMetadata);
        var package = new ModPawnExchangePackageDto
        {
            PackageVersion = 1,
            Reference = reference,
            Identity = BuildPawnExchangeIdentity(pawn),
            Appearance = new ModPawnExchangeAppearanceDto
            {
                DisplayName = pawn.LabelShort,
                BodyTypeDef = pawn.story?.bodyType?.defName,
                HeadTypeDef = pawn.story?.headType?.defName,
                HairDef = pawn.story?.hairDef?.defName,
                BeardDef = pawn.style?.beardDef?.defName,
                SkinColor = null,
                HairColor = null
            },
            Status = new ModPawnExchangeStatusDto
            {
                Dead = pawn.Dead,
                BiologicalAgeTicks = pawn.ageTracker?.AgeBiologicalTicks,
                ChronologicalAgeTicks = pawn.ageTracker?.AgeChronologicalTicks,
                DeathCauseDef = null,
                HealthState = pawn.health?.State.ToString()
            },
            Apparel = pawn.apparel?.WornApparel
                .Select(apparel => ToPawnExchangeEquipmentItem(apparel, assignment, pawn, "apparel"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Equipment = pawn.equipment?.AllEquipmentListForReading
                .Select(equipment => ToPawnExchangeEquipmentItem(equipment, assignment, pawn, "equipment"))
                .ToList() ?? new List<ModPawnExchangeEquipmentItemDto>(),
            Relationships = BuildOneLayerRelationshipStubs(pawn),
            Scribe = BuildPawnScribePayload(pawn, BuildRelatedPawnGlobalKey)
        };
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, package);
        return package;
    }

    private ModPawnExchangeEquipmentItemDto ToPawnExchangeEquipmentItem(
        Thing thing,
        Caravan caravan,
        Pawn ownerPawn,
        string container)
    {
        QualityCategory quality;
        string? qualityValue = QualityUtility.TryGetQuality(thing, out quality)
            ? quality.ToString()
            : null;
        Apparel? apparel = thing as Apparel;
        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        bool traitWeapon = TradeThingReferenceUtility.IsWeaponWithTraits(thing);

        return new ModPawnExchangeEquipmentItemDto
        {
            GlobalId = $"{BuildPawnGlobalKey(ownerPawn, caravan)}/{container}:{thing.ThingID}",
            Def = thing.def?.defName,
            Label = thing.LabelCapNoCount,
            StackCount = Math.Max(1, thing.stackCount),
            Quality = qualityValue,
            HitPoints = thing.def?.useHitPoints == true ? thing.HitPoints : null,
            WornByCorpse = apparel?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnGlobalId = biocoded ? BuildBiocodedPawnGlobalId(biocodable?.CodedPawn, caravan) : null,
            UniqueWeapon = traitWeapon ? true : null,
            UniqueWeaponName = null,
            UniqueWeaponTraits = TradeThingReferenceUtility.WeaponTraitDefNames(thing)
        };
    }

    private ModPawnExchangeEquipmentItemDto ToPawnExchangeEquipmentItem(
        Thing thing,
        ActiveSupportPawnAssignment assignment,
        Pawn ownerPawn,
        string container)
    {
        QualityCategory quality;
        string? qualityValue = QualityUtility.TryGetQuality(thing, out quality)
            ? quality.ToString()
            : null;
        Apparel? apparel = thing as Apparel;
        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        bool traitWeapon = TradeThingReferenceUtility.IsWeaponWithTraits(thing);

        return new ModPawnExchangeEquipmentItemDto
        {
            GlobalId = $"{assignment.PawnGlobalKey}/{container}:{thing.ThingID}",
            Def = thing.def?.defName,
            Label = thing.LabelCapNoCount,
            StackCount = Math.Max(1, thing.stackCount),
            Quality = qualityValue,
            HitPoints = thing.def?.useHitPoints == true ? thing.HitPoints : null,
            WornByCorpse = apparel?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnGlobalId = biocoded ? BuildRelatedPawnGlobalKey(biocodable?.CodedPawn ?? ownerPawn) : null,
            UniqueWeapon = traitWeapon ? true : null,
            UniqueWeaponName = null,
            UniqueWeaponTraits = TradeThingReferenceUtility.WeaponTraitDefNames(thing)
        };
    }

    private List<ModPawnExchangeRelationshipStubDto> BuildOneLayerRelationshipStubs(Pawn pawn)
    {
        if (pawn.relations?.DirectRelations is null)
        {
            return new List<ModPawnExchangeRelationshipStubDto>();
        }

        return pawn.relations.DirectRelations
            .Where(relation => relation?.otherPawn is not null && !string.IsNullOrWhiteSpace(relation.otherPawn.ThingID))
            .Take(128)
            .Select(relation => new ModPawnExchangeRelationshipStubDto
            {
                OtherPawnGlobalId = BuildRelatedPawnGlobalKey(relation.otherPawn),
                OtherPawnName = relation.otherPawn.LabelShort,
                OtherPawnDead = relation.otherPawn.Dead,
                RelationDef = relation.def?.defName
            })
            .ToList();
    }

    private string? BuildBiocodedPawnGlobalId(Pawn? pawn, Caravan caravan)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return caravan.ContainsPawn(pawn)
            ? BuildPawnGlobalKey(pawn, caravan)
            : BuildRelatedPawnGlobalKey(pawn);
    }

    private ModPawnScribePayloadDto? BuildPawnScribePayload(Pawn pawn, Func<Pawn, string> globalIdResolver)
    {
        try
        {
            string? xml = TryCreatePawnScribeXml(pawn);
            if (string.IsNullOrWhiteSpace(xml))
            {
                Log.Warning("[ClashOfRim] Pawn Scribe debug output was empty; support package will use structured fields only.");
                return null;
            }

            string scribeXml = xml!;
            return new ModPawnScribePayloadDto
            {
                Xml = scribeXml,
                XmlSha256 = ComputeSha256Hex(scribeXml),
                PawnReferenceReplacements = BuildScribePawnReferenceReplacements(pawn, globalIdResolver)
            };
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            Log.Warning("[ClashOfRim] Failed to build pawn Scribe payload: " + ex);
            return null;
        }
    }

    private static string? TryCreatePawnScribeXml(Pawn pawn)
    {
        FieldInfo? saverField = typeof(Scribe).GetField("saver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? saver = saverField?.GetValue(null);
        if (saver is null)
        {
            return null;
        }

        MethodInfo? method = saver.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "DebugOutputFor", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(Pawn));
            });
        return method?.Invoke(saver, new object[] { pawn }) as string;
    }

    private List<ModPawnScribePawnReferenceReplacementDto> BuildScribePawnReferenceReplacements(Pawn pawn, Func<Pawn, string> globalIdResolver)
    {
        var replacements = new Dictionary<string, ModPawnScribePawnReferenceReplacementDto>(StringComparer.Ordinal);
        void AddReferencedPawn(Pawn? referencedPawn)
        {
            if (referencedPawn is null || referencedPawn == pawn)
            {
                return;
            }

            string sourceLoadId = referencedPawn.GetUniqueLoadID();
            if (string.IsNullOrWhiteSpace(sourceLoadId) || replacements.ContainsKey(sourceLoadId))
            {
                return;
            }

            string globalId = globalIdResolver(referencedPawn);
            replacements.Add(sourceLoadId, new ModPawnScribePawnReferenceReplacementDto
            {
                SourceLoadId = sourceLoadId,
                PlaceholderLoadId = "ClashOfRimPlaceholderPawn_" + ShortHash(globalId),
                Reference = BuildPawnReference(referencedPawn, globalId)
            });
        }

        if (pawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations)
            {
                AddReferencedPawn(relation?.otherPawn);
            }
        }

        foreach (ThingWithComps item in EnumeratePawnEquipmentWithComps(pawn))
        {
            AddReferencedPawn(item.TryGetComp<CompBiocodable>()?.CodedPawn);
        }

        return replacements.Values.Take(128).ToList();
    }

    private static IEnumerable<ThingWithComps> EnumeratePawnEquipmentWithComps(Pawn pawn)
    {
        if (pawn.apparel?.WornApparel is not null)
        {
            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                yield return apparel;
            }
        }

        if (pawn.equipment?.AllEquipmentListForReading is not null)
        {
            foreach (ThingWithComps equipment in pawn.equipment.AllEquipmentListForReading)
            {
                yield return equipment;
            }
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return ToHexLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string ShortHash(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return ToHexLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(text))).Substring(0, 16);
    }

    private static string ToHexLower(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
