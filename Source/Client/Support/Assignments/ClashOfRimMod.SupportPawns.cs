using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using System.Collections.Generic;
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
    internal void OpenCaravanSupportPawnMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, IReadOnlyList<Pawn> pawns) =>
        supportActions.OpenMenu(caravan, targets, pawns);

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
