using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class ClientSupportReturnRuntime : IClientSupportReturnRuntime
{
    private readonly IClientSupportPresentation presentation;
    internal ClientSupportReturnRuntime(IClientSupportPresentation presentation) => this.presentation = presentation;
    public IClientSupportReturnSource? Find(string eventId)
    {
        Game? game = Current.Game;
        var component = game?.GetComponent<ClashOfRimGameComponent>();
        var assignment = component?.FindSupportAssignmentByEventId(eventId);
        return game is null || component is null || assignment is null ? null
            : new Source(game, component, assignment, SupportPawnLookup.Find(assignment.PawnThingId), presentation);
    }

    private sealed class Source : IClientSupportReturnSource
    {
        private readonly Game game;
        private readonly ClashOfRimGameComponent component;
        private readonly ActiveSupportPawnAssignment assignment;
        private readonly Pawn? pawn;
        private readonly IClientSupportPresentation presentation;
        internal Source(Game game, ClashOfRimGameComponent component, ActiveSupportPawnAssignment assignment, Pawn? pawn, IClientSupportPresentation presentation)
        { this.game = game; this.component = component; this.assignment = assignment; this.pawn = pawn; this.presentation = presentation; }
        public ClientSupportReturnView View => new(assignment.PermanentSupport, assignment.FinishInProgress || assignment.ReturnPrepared,
            pawn is not null, assignment.ReturnPrepared || pawn is not null && (pawn.Dead || !pawn.Spawned));

        private void RequireCurrent()
        {
            if (!ReferenceEquals(Current.Game, game) || !ReferenceEquals(component.FindSupportAssignmentByEventId(assignment.EventId), assignment))
                throw new InvalidOperationException("Support return no longer belongs to the captured game/assignment.");
        }
        public bool Depart(string reason, Action beforeMutation)
        {
            RequireCurrent();
            if (pawn is null) throw new InvalidOperationException("Support pawn is missing.");
            beforeMutation();
            component.MarkSupportAssignmentInProgress(assignment.EventId, true);
            assignment.ReturnFinishReason = reason;
            if (pawn.Dead) return true;
            PawnExchangeLifecycleService.StripExchangeTags(pawn);
            PrepareSupportPawnForReturn(pawn, SupportPawnFactionUtility.ResolveOriginalFaction(assignment));
            if (pawn.drafter is not null) pawn.drafter.Drafted = false;
            if (!pawn.Spawned || pawn.MapHeld is null) return true;
            LordMaker.MakeNewLord(pawn.Faction,
                new SupportPawnReturnLordJob(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true), pawn.MapHeld, Gen.YieldSingle(pawn));
            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            string message = presentation.Text("StatusReturnDeparting", ("PAWN", pawn.LabelShort));
            presentation.Status(message); Messages.Message(message, pawn, MessageTypeDefOf.NeutralEvent, historical: false);
            return false;
        }
        public ClientSupportReturnPayload Prepare(ClashOfRimClientNetworkContext context, string reason, Action beforeMutation)
        {
            RequireCurrent();
            if (!assignment.ReturnPrepared)
            {
                if (pawn is null || !View.CanFinish) throw new InvalidOperationException("Support pawn has not departed.");
                var package = pawn.Dead ? null : new PawnExchangeCapture(context.UserId, context.ColonyId, context.CurrentSnapshotId)
                    .Capture(pawn, assignment.PawnGlobalKey, preservedMetadata: assignment.PawnReferenceMetadata);
                if (!pawn.Dead && (package?.Scribe is null || string.IsNullOrWhiteSpace(package.Scribe.Xml)))
                    throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusMissingScribe",
                        ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelSupport").Named("LABEL")));
                string? json = package is null ? null : ClientProtocolJson.Serialize(package);
                beforeMutation();
                assignment.ReturnFinishReason = string.IsNullOrWhiteSpace(assignment.ReturnFinishReason) ? reason : assignment.ReturnFinishReason;
                assignment.PawnLabel = pawn.LabelShort;
                assignment.ReturnPawnDead = pawn.Dead;
                assignment.ReturnPackageJson = json;
                if (!pawn.Destroyed && !PawnExchangeLifecycleService.RemoveFromLocalWorld(pawn))
                    throw new InvalidOperationException(presentation.Text("StatusReturnLocalRemoveFailed", ("PAWN", pawn.LabelShort)));
                // Keep this outbox in the confirmation save. A reload may replay the
                // idempotent report, but must never recapture or remove the pawn.
                assignment.ReturnPrepared = true;
            }
            return new(assignment.EventId, assignment.PawnGlobalKey, assignment.PawnLabel, assignment.ReturnFinishReason,
                assignment.ReturnPawnDead, assignment.ReturnPawnDead ? null : ClientProtocolJson.Deserialize<ModPawnExchangePackageDto>(
                    assignment.ReturnPackageJson ?? "", type => "Invalid prepared support return: " + type));
        }
        public void Complete()
        {
            RequireCurrent();
            component.MarkSupportAssignmentFinished(assignment.EventId);
            ClashSupportPawnQuestUtility.CompleteSupportQuest(assignment.EventId);
        }
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

}
