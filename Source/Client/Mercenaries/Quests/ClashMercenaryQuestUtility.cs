using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Quests;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal static class ClashMercenaryQuestUtility
{
    public const string ServerMercenaryFactionDefName = "ClashOfRim_ServerMercenaries";

    public static void CreateMercenaryQuest(ModMercenaryContractDto contract, Pawn pawn)
    {
        string skillLabel = MercenarySkillUtility.ProfessionLabel(contract.SkillDefName);
        Quest quest = ClashManagedQuestUtility.CreateRawManagedQuest(
            ClashOfRimQuestDefOf.ClashOfRim_Mercenary,
            ClashOfRimText.Key(
                "ClashOfRim.Mercenary.QuestName",
                pawn.LabelShort.Named("PAWN"),
                skillLabel.Named("SKILL")),
            ClashOfRimText.Key(
                "ClashOfRim.Mercenary.QuestFullDescription",
                pawn.LabelShort.Named("PAWN"),
                skillLabel.Named("SKILL"),
                MercenarySkillUtility.TierLabel(contract.SkillLevel).Named("TIER"),
                contract.DurationDays.Named("DAYS"),
                contract.PriceSilver.Named("PRICE"),
                contract.HarmfulSurgeryFineSilver.Named("SURGERYFINE"),
                contract.DeathFineSilver.Named("DEATHFINE")));

        var part = new QuestPart_ClashMercenary
        {
            ContractId = contract.ContractId,
            Pawn = pawn,
            PawnLabel = pawn.LabelShort,
            SkillDefName = contract.SkillDefName,
            SkillLevel = contract.SkillLevel,
            PriceSilver = contract.PriceSilver,
            HarmfulSurgeryFineSilver = contract.HarmfulSurgeryFineSilver,
            DeathFineSilver = contract.DeathFineSilver,
            ExpiresAtGameTicks = contract.ExpiresAtGameTicks,
            QuestTag = MercenarySkillUtility.QuestTag(contract.ContractId),
            inSignalEnable = quest.InitiateSignal
        };
        quest.AddPart(part);

        Faction? faction = EnsureServerMercenaryFaction();
        if (faction is not null)
        {
            quest.AddPart(new QuestPart_ExtraFaction
            {
                extraFaction = new ExtraFaction(faction, ExtraFactionType.HomeFaction),
                affectedPawns = new List<Pawn> { pawn },
                areHelpers = true,
                inSignalEnable = quest.InitiateSignal
            });
        }

        ClashManagedQuestUtility.AddAcceptedManagedQuest(quest);
    }

    public static bool TrySendArrivalShuttle(Pawn pawn, out string message)
    {
        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusNoMap");
            return false;
        }

        Faction? faction = EnsureServerMercenaryFaction();
        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction is not null)
        {
            shuttle.SetFaction(faction);
        }

        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            Gen.YieldSingle<Thing>(pawn),
            shuttle);
        ship.ArriveAt(DropCellFinder.GetBestShuttleLandingSpot(map, faction ?? Faction.OfPlayer), map.Parent);
        ship.AddJobs(ShipJobDefOf.Unload, ShipJobDefOf.FlyAway);
        message = string.Empty;
        return true;
    }

    public static bool TryRecallMercenary(
        Pawn pawn,
        string questTag,
        out bool waitsForLoading,
        out Thing? recallShuttle,
        out TransportShip? recallShip)
    {
        waitsForLoading = false;
        recallShuttle = null;
        recallShip = null;
        Map? map = pawn.MapHeld ?? Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            RemovePawn(pawn);
            return true;
        }

        Faction? faction = EnsureServerMercenaryFaction();
        Thing shuttle = ThingMaker.MakeThing(ThingDefOf.Shuttle);
        if (faction is not null)
        {
            shuttle.SetFaction(faction);
        }

        CompShuttle? shuttleComp = shuttle.TryGetComp<CompShuttle>();
        if (shuttleComp is not null)
        {
            shuttleComp.requiredPawns.Add(pawn);
        }
        if (!string.IsNullOrWhiteSpace(questTag))
        {
            QuestUtility.AddQuestTag(ref shuttle.questTags, questTag);
        }

        TransportShip ship = TransportShipMaker.MakeTransportShip(
            TransportShipDefOf.Ship_Shuttle,
            null,
            shuttle);
        if (!string.IsNullOrWhiteSpace(questTag))
        {
            QuestUtility.AddQuestTag(ref ship.questTags, questTag);
        }
        ship.ArriveAt(DropCellFinder.GetBestShuttleLandingSpot(map, faction ?? Faction.OfPlayer), map.Parent);
        ShipJob_WaitForever waitJob = (ShipJob_WaitForever)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitForever);
        waitJob.leaveImmediatelyWhenSatisfied = true;
        ship.AddJob(waitJob);
        recallShuttle = shuttle;
        recallShip = ship;
        waitsForLoading = true;
        return true;
    }

    public static bool TryStartRecallShuttleLoading(
        QuestPart_ClashMercenary part,
        CompTransporter transporter,
        out string message,
        out MessageTypeDef messageType)
    {
        messageType = MessageTypeDefOf.TaskCompletion;
        Pawn? pawn = part.Pawn;
        if (pawn is null || pawn.Destroyed || pawn.Dead)
        {
            message = ClashOfRimText.Key("ClashOfRim.Mercenary.LoadRecallShuttlePawnMissing");
            messageType = MessageTypeDefOf.RejectInput;
            return false;
        }

        ThingWithComps? shuttleThing = transporter.parent;
        if (shuttleThing is null || shuttleThing.Destroyed || !shuttleThing.Spawned)
        {
            message = ClashOfRimText.Key("ClashOfRim.Mercenary.LoadRecallShuttleMissing");
            messageType = MessageTypeDefOf.RejectInput;
            return false;
        }

        if (transporter.innerContainer.Contains(pawn))
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.Mercenary.LoadRecallShuttleAlreadyLoaded",
                pawn.LabelShort.Named("PAWN"));
            return true;
        }

        CompShuttle? shuttle = shuttleThing.TryGetComp<CompShuttle>();
        if (shuttle is not null && !shuttle.requiredPawns.Contains(pawn))
        {
            shuttle.requiredPawns.Add(pawn);
        }

        if (!transporter.LoadingInProgressOrReadyToLaunch)
        {
            TransporterUtility.InitiateLoading(Gen.YieldSingle(transporter));
        }

        EnsurePawnLeftToLoad(transporter, pawn);
        Map? map = shuttleThing.MapHeld ?? pawn.MapHeld;
        if (map is null || !pawn.Spawned)
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.Mercenary.LoadRecallShuttleQueued",
                pawn.LabelShort.Named("PAWN"));
            return true;
        }

        List<CompTransporter> transporters = transporter.TransportersInGroup(map)?.ToList()
            ?? new List<CompTransporter> { transporter };
        if (!pawn.Downed
            && pawn.CanReach(
                shuttleThing,
                PathEndMode.Touch,
                Danger.Deadly,
                canBashDoors: false,
                canBashFences: false,
                TraverseMode.ByPawn))
        {
            TransporterUtility.MakeLordsAsAppropriate(new List<Pawn> { pawn }, transporters, map);
            Job job = JobMaker.MakeJob(JobDefOf.EnterTransporter, shuttleThing);
            pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
            message = ClashOfRimText.Key(
                "ClashOfRim.Mercenary.LoadRecallShuttleStarted",
                pawn.LabelShort.Named("PAWN"));
            return true;
        }

        message = ClashOfRimText.Key(
            "ClashOfRim.Mercenary.LoadRecallShuttleHaulQueued",
            pawn.LabelShort.Named("PAWN"));
        return true;
    }

    public static QuestPart_ClashMercenary? FindActivePartForPawn(Pawn pawn)
    {
        return Find.QuestManager?.QuestsListForReading?
            .Where(quest => quest.State == QuestState.Ongoing)
            .SelectMany(quest => quest.PartsListForReading.OfType<QuestPart_ClashMercenary>())
            .FirstOrDefault(part => part.Pawn == pawn && !part.Completed);
    }

    private static void EnsurePawnLeftToLoad(CompTransporter transporter, Pawn pawn)
    {
        if (transporter.LeftToLoadContains(pawn))
        {
            return;
        }

        TransferableOneWay transferable = new();
        transferable.things.Add(pawn);
        transporter.AddToTheToLoadList(transferable, 1);
    }

    public static void ReleaseMercenaryAfterRecallShuttleDestroyed(Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            return;
        }

        Faction? faction = EnsureServerMercenaryFaction();
        if (faction is not null && pawn.Faction != faction)
        {
            pawn.SetFaction(faction);
        }

        if (pawn.drafter is not null)
        {
            pawn.drafter.Drafted = false;
        }

        if (!pawn.Spawned || pawn.MapHeld is null)
        {
            return;
        }

        Lord? currentLord = pawn.GetLord();
        currentLord?.Notify_PawnLost(pawn, PawnLostCondition.LeftVoluntarily, null);
        LordMaker.MakeNewLord(
            pawn.Faction,
            new LordJob_ExitMapBest(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true),
            pawn.MapHeld,
            Gen.YieldSingle(pawn));
        pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
    }

    private static Faction? EnsureServerMercenaryFaction()
    {
        if (Find.World?.factionManager is null)
        {
            return null;
        }

        Faction? existing = Find.World.factionManager.AllFactions.FirstOrDefault(faction =>
            string.Equals(faction.def?.defName, ServerMercenaryFactionDefName, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.hidden = false;
            return existing;
        }

        FactionDef? factionDef = DefDatabase<FactionDef>.GetNamedSilentFail(ServerMercenaryFactionDefName);
        if (factionDef is null)
        {
            return null;
        }

        Faction faction = new()
        {
            def = factionDef,
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            temporary = true,
            hidden = false,
            allowGoodwillRewards = false,
            allowRoyalFavorRewards = false,
            color = new Color(0.78f, 0.66f, 0.32f)
        };
        faction.Name = ClashOfRimText.Key("ClashOfRim.Mercenary.ServerFactionName");
        ClashOfRimCompatibilityApi.NotifyFactionPrepared(faction, "ServerMercenary");

        foreach (Faction other in Find.World.factionManager.AllFactionsListForReading.ToList())
        {
            faction.SetRelation(new FactionRelation(other, FactionRelationKind.Neutral));
            other.SetRelation(new FactionRelation(faction, FactionRelationKind.Neutral));
        }

        Find.World.factionManager.Add(faction);
        return faction;
    }

    private static void RemovePawn(Pawn pawn)
    {
        if (pawn.Spawned)
        {
            pawn.DeSpawn();
        }

        if (!pawn.Destroyed)
        {
            pawn.Destroy(DestroyMode.Vanish);
        }
    }
}
