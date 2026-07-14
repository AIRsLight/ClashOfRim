using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

public static class GiftLandingApplicator
{
    private const string CaravanDeliveryPointDefName = "ClashOfRim_CaravanDeliveryPoint";
    private const int DropPodOpenDelayTicks = 110;
    private static Dictionary<ThingDef, PawnKindDef>? animalKindByRace;
    private static readonly FieldInfo? CorpseVanishAfterTimestampField = typeof(Corpse).GetField(
        "vanishAfterTimestamp",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static GiftLandingApplicationResult ApplyToCurrentMap(GiftLandingPlan? plan)
    {
        return ApplyToMap(plan, Find.CurrentMap);
    }

    public static GiftLandingApplicationResult ApplyToMap(GiftLandingPlan? plan, Map? map)
    {
        if (plan is null)
        {
            Log.Warning("[ClashOfRim][GiftLanding] plan is null.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.MissingPlan,
                string.Empty,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusMissingPlan"));
        }

        if (map is null)
        {
            Log.Warning($"[ClashOfRim][GiftLanding] event={plan.EventId} no target map.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.MissingCurrentMap,
                plan.EventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusMissingCurrentMap"));
        }

        string currentMapId = CurrentMapLoadId(map);
        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] start event={plan.EventId} currentMap={currentMapId} targetMap={plan.TargetMapUniqueId} landingMode={plan.LandingMode ?? "<null>"} items={plan.Items.Count}.");
        if (!MapIdsMatch(currentMapId, plan.TargetMapUniqueId))
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] map mismatch event={plan.EventId} currentMap={currentMapId} targetMap={plan.TargetMapUniqueId}.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.MapMismatch,
                plan.EventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusMapMismatch",
                    currentMapId.Named("CURRENT"),
                    plan.TargetMapUniqueId.Named("TARGET")));
        }

        if (plan.Items.Count == 0)
        {
            Log.Warning($"[ClashOfRim][GiftLanding] event={plan.EventId} has no gift items.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.EmptyGiftItems,
                plan.EventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusNoItems"));
        }

        var preparedThings = new List<Thing>();
        foreach (GiftItemReference item in plan.Items)
        {
            if (item.SkipLanding)
            {
                Log.Warning($"[ClashOfRim][GiftLanding] skip item event={plan.EventId} key={item.GlobalKey} reason={item.SkipReason ?? "<null>"}.");
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.ThingCreationFailed,
                    plan.EventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusSkippedItem",
                        (item.DisplayLabel ?? item.DefName ?? item.GlobalKey).Named("THING"),
                        (item.SkipReason ?? string.Empty).Named("MESSAGE")));
            }

            ClashLog.Message(
                $"[ClashOfRim][GiftLanding] prepare item event={plan.EventId} key={item.GlobalKey} def={item.DefName ?? "<null>"} count={item.StackCount} sourceSnapshot={item.SourceSnapshotId ?? "<null>"}.");
            GiftLandingApplicationResult? validationFailure = TryPrepareThings(item, plan.EventId, preparedThings);
            if (validationFailure is not null)
            {
                Log.Warning(
                    $"[ClashOfRim][GiftLanding] prepare failed event={plan.EventId} kind={validationFailure.Kind} message={validationFailure.Message}");
                return validationFailure;
            }
        }

        if (preparedThings.Count == 0)
        {
            Log.Warning($"[ClashOfRim][GiftLanding] event={plan.EventId} prepared zero things.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.EmptyGiftItems,
                plan.EventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusNoPreparedItems"));
        }

        GiftLandingApplicationResult? applyResult = ApplyPreparedThings(plan, map, preparedThings);
        if (applyResult is not null)
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] apply failed event={plan.EventId} kind={applyResult.Kind} message={applyResult.Message}");
            return applyResult;
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] applied event={plan.EventId} itemEntries={plan.Items.Count} preparedStacks={preparedThings.Count} mode={ResolveLandingMode(plan.LandingMode)}.");
        return GiftLandingApplicationResult.Applied(
            plan.EventId,
            plan.Items.Count,
            preparedThings.Count,
            ResolveLandingMode(plan.LandingMode),
            plan.RequiresSnapshotConfirmation);
    }

    private static GiftLandingApplicationResult? ApplyPreparedThings(
        GiftLandingPlan plan,
        Map map,
        IReadOnlyList<Thing> preparedThings)
    {
        string landingMode = ResolveLandingMode(plan.LandingMode);
        if (landingMode == "DropPod")
        {
            return DropByPod(plan, map, preparedThings);
        }

        IntVec3 rootCell = FindDirectDeliveryCell(map, landingMode);
        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] placing event={plan.EventId} mode={landingMode} rootCell={rootCell} stacks={preparedThings.Count}.");

        var placedThings = new List<Thing>();
        var arrivalTargets = new List<Thing>();
        foreach (Thing thing in preparedThings)
        {
            bool placed;
            Thing? notificationTarget = null;
            try
            {
                placed = GenPlace.TryPlaceThing(
                    thing,
                    rootCell,
                    map,
                    ThingPlaceMode.Near,
                    placedAction: (resultingThing, _) =>
                    {
                        notificationTarget ??= resultingThing;
                        AddArrivalTarget(arrivalTargets, resultingThing);
                    });
            }
            catch (Exception ex)
            {
                RollBackPlacedThings(placedThings);
                Log.Warning(
                    $"[ClashOfRim][GiftLanding] TryPlaceThing threw event={plan.EventId} def={thing.def?.defName ?? "<unknown>"} exception={ex}");
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.PlacementFailed,
                    plan.EventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusPlacementException",
                        (thing.def?.defName ?? "<unknown>").Named("THING"),
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE")));
            }

            if (!placed)
            {
                RollBackPlacedThings(placedThings);
                Log.Warning(
                    $"[ClashOfRim][GiftLanding] TryPlaceThing returned false event={plan.EventId} def={thing.def?.defName ?? "<unknown>"} rootCell={rootCell}.");
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.PlacementFailed,
                    plan.EventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusPlacementFailed",
                        (thing.def?.defName ?? "<unknown>").Named("THING")));
            }

            placedThings.Add(thing);
            ClashLog.Message(
                $"[ClashOfRim][GiftLanding] placed event={plan.EventId} thing={thing.def?.defName ?? "<unknown>"} stack={thing.stackCount} spawned={thing.Spawned} pos={thing.Position} map={CurrentMapLoadId(map)}.");
            NotifyDirectPlacement(plan.EventId, notificationTarget ?? thing);
        }

        if (arrivalTargets.Count > 0)
        {
            NotifyArrivalLetter(plan, arrivalTargets);
        }

        return null;
    }

    private static GiftLandingApplicationResult? DropByPod(
        GiftLandingPlan plan,
        Map map,
        IReadOnlyList<Thing> preparedThings)
    {
        IntVec3 dropCenter = FindDropPodLandingCell(map, out bool allowRoofPunch);
        if (!dropCenter.IsValid)
        {
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.PlacementFailed,
                plan.EventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusDropCellMissing"));
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] dropping by pod event={plan.EventId} center={dropCenter} stacks={preparedThings.Count}.");
        try
        {
            DropPodUtility.DropThingsNear(
                dropCenter,
                map,
                preparedThings,
                openDelay: DropPodOpenDelayTicks,
                canInstaDropDuringInit: false,
                leaveSlag: false,
                canRoofPunch: allowRoofPunch,
                forbid: false,
                allowFogged: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim][GiftLanding] DropThingsNear threw event={plan.EventId} exception={ex}");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.PlacementFailed,
                plan.EventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusDropPodException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE")));
        }

        // DropThingsNear has created the transport pods at this point.  Confirmation is
        // intentionally anchored to pod creation, not to each pod opening later, because
        // open timing can differ by map tick state while the payload is already committed.
        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] DropThingsNear returned event={plan.EventId}; drop pods were created and snapshot can be confirmed.");
        NotifyDropPodPlacement(plan.EventId, dropCenter, map);
        NotifyArrivalLetter(plan, preparedThings);

        return null;
    }

    private static GiftLandingApplicationResult? TryPrepareThings(
        GiftItemReference item,
        string eventId,
        List<Thing> preparedThings)
    {
        if (string.IsNullOrWhiteSpace(item.DefName))
        {
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.MissingThingDef,
                eventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusMissingDefName", item.GlobalKey.Named("THING")));
        }

        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(item.DefName);
        if (def is null)
        {
            Log.Warning($"[ClashOfRim][GiftLanding] missing ThingDef event={eventId} def={item.DefName}");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.MissingThingDef,
                eventId,
                ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusThingDefMissing", item.DefName.Named("THING")));
        }

        if (item.StackCount <= 0)
        {
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.InvalidStackCount,
                eventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusInvalidStackCount",
                    item.DefName.Named("THING"),
                    item.StackCount.Named("COUNT")));
        }

        int remaining = item.StackCount;
        int stackLimit = Math.Max(1, def.stackLimit);
        ClashLog.Message(
            $"[ClashOfRim][GiftLanding] ThingDef resolved event={eventId} def={def.defName} stackLimit={stackLimit} total={item.StackCount}.");
        if (def.IsCorpse)
        {
            if (item.PawnPackage is null)
            {
                Log.Warning(
                    $"[ClashOfRim][GiftLanding] corpse gift item has no inner pawn package event={eventId} def={def.defName} key={item.GlobalKey} pawnPackageId={item.PawnPackageId ?? "<null>"}.");
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.PawnRestoreFailed,
                    eventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusCorpsePackageMissing",
                        item.DefName.Named("THING")));
            }

            if (GiftPawnPackageUtility.TryRestoreCorpsePawn(item.PawnPackage, out Pawn? corpsePawn, out string corpseRestoreMessage)
                && corpsePawn is not null)
            {
                Corpse? corpse = corpsePawn.MakeCorpse(null, inBed: false, bedRotation: 0f);
                if (corpse is not null)
                {
                    ApplyCorpseMetadata(item, corpse);
                    preparedThings.Add(corpse);
                    ClashLog.Message($"[ClashOfRim][GiftLanding] restored corpse event={eventId} def={def.defName}: {corpseRestoreMessage}");
                    return null;
                }

                corpseRestoreMessage = ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusCorpseCreationFailed",
                    item.DefName.Named("THING"));
            }

            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.PawnRestoreFailed,
                eventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusCorpseRestoreFailed",
                    item.DefName.Named("THING"),
                    corpseRestoreMessage.Named("MESSAGE")));
        }

        if (def.category == ThingCategory.Pawn && item.PawnPackage is null)
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] pawn gift/trade item has no pawn package event={eventId} def={def.defName} key={item.GlobalKey} pawnPackageId={item.PawnPackageId ?? "<null>"}.");
            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.PawnRestoreFailed,
                eventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusPawnPackageMissing",
                    item.DefName.Named("THING")));
        }

        if (def.category == ThingCategory.Pawn && item.PawnPackage is not null)
        {
            ClashLog.Message(
                $"[ClashOfRim][GiftLanding] restoring gift pawn event={eventId} def={def.defName} key={item.GlobalKey}.");
            bool restored = GiftPawnPackageUtility.TryRestoreGiftPawn(item.PawnPackage, out Pawn? restoredPawn, out string pawnRestoreMessage)
                || GiftPawnPackageUtility.TryRestoreTradePawn(item.PawnPackage, out restoredPawn, out pawnRestoreMessage);
            if (restored
                && restoredPawn is not null)
            {
                NormalizeGiftPawnForReceiver(restoredPawn);
                preparedThings.Add(restoredPawn);
                ClashLog.Message($"[ClashOfRim][GiftLanding] restored gift pawn event={eventId} def={def.defName}: {pawnRestoreMessage}");
                return null;
            }

            return GiftLandingApplicationResult.Failed(
                GiftLandingApplicationResultKind.PawnRestoreFailed,
                eventId,
                ClashOfRimText.Key(
                    "ClashOfRim.GiftLanding.StatusPawnRestoreFailed",
                    item.DefName.Named("THING"),
                    pawnRestoreMessage.Named("MESSAGE")));
        }

        if (def.race?.Animal == true)
        {
            string restoreMessage = string.Empty;
            if (item.PawnPackage is not null
                && GiftPawnPackageUtility.TryRestore(item.PawnPackage, out Pawn? restoredAnimal, out restoreMessage)
                && restoredAnimal is not null)
            {
                preparedThings.Add(restoredAnimal);
                ClashLog.Message($"[ClashOfRim][GiftLanding] restored animal event={eventId} def={def.defName}: {restoreMessage}");
                return null;
            }

            if (item.PawnPackage is not null)
            {
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.PawnRestoreFailed,
                    eventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusAnimalRestoreFailed",
                        item.DefName.Named("THING"),
                        restoreMessage.Named("MESSAGE")));
            }

            PawnKindDef? animalKind = ResolveAnimalPawnKind(def);
            if (animalKind is null)
            {
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.ThingCreationFailed,
                    eventId,
                    ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusAnimalKindMissing", item.DefName.Named("THING")));
            }

            while (remaining > 0)
            {
                Pawn animal = PawnGenerator.GeneratePawn(animalKind, Faction.OfPlayer);
                preparedThings.Add(animal);
                ClashLog.Message(
                    $"[ClashOfRim][GiftLanding] prepared animal event={eventId} def={def.defName} pawnKind={animalKind.defName} remainingAfter={remaining - 1}.");
                remaining--;
            }

            return null;
        }

        while (remaining > 0)
        {
            int stackCount = Math.Min(remaining, stackLimit);
            Thing? thing;
            try
            {
                if (!TradeThingReferenceUtility.TryMakeThing(
                        ToModThingReference(item),
                        stackCount,
                        out thing,
                        out string? missingDefName))
                {
                    return GiftLandingApplicationResult.Failed(
                        GiftLandingApplicationResultKind.ThingCreationFailed,
                        eventId,
                        ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusThingCreationFailed", (missingDefName ?? item.DefName).Named("THING")));
                }
            }
            catch (Exception ex)
            {
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.ThingCreationFailed,
                    eventId,
                    ClashOfRimText.Key(
                        "ClashOfRim.GiftLanding.StatusThingCreationException",
                        item.DefName.Named("THING"),
                        ex.GetType().Name.Named("TYPE"),
                        ex.Message.Named("MESSAGE")));
            }

            if (thing is null)
            {
                return GiftLandingApplicationResult.Failed(
                    GiftLandingApplicationResultKind.ThingCreationFailed,
                    eventId,
                    ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusThingCreationFailed", item.DefName.Named("THING")));
            }

            preparedThings.Add(thing);
            ClashLog.Message(
                $"[ClashOfRim][GiftLanding] prepared thing event={eventId} def={def.defName} stack={stackCount} remainingAfter={remaining - stackCount}.");
            remaining -= stackCount;
        }

        return null;
    }

    private static PawnKindDef? ResolveAnimalPawnKind(ThingDef raceDef)
    {
        animalKindByRace ??= DefDatabase<PawnKindDef>.AllDefsListForReading
            .Where(kind => kind.race is not null)
            .GroupBy(kind => kind.race)
            .ToDictionary(group => group.Key, group => group.First());

        return animalKindByRace.TryGetValue(raceDef, out PawnKindDef? animalKind)
            ? animalKind
            : null;
    }

    private static ModThingReferenceDto ToModThingReference(GiftItemReference item)
    {
        var reference = new ModThingReferenceDto
        {
            GlobalKey = item.GlobalKey,
            DefName = item.DefName,
            StackCount = Math.Max(1, item.StackCount),
            Quality = item.Quality,
            HitPoints = item.HitPoints,
            StuffDefName = item.StuffDefName,
            MaxHitPoints = item.MaxHitPoints,
            MinifiedInnerDefName = item.MinifiedInnerDefName,
            MinifiedInnerStuffDefName = item.MinifiedInnerStuffDefName,
            MinifiedInnerQuality = item.MinifiedInnerQuality,
            MinifiedInnerHitPoints = item.MinifiedInnerHitPoints,
            MinifiedInnerMaxHitPoints = item.MinifiedInnerMaxHitPoints,
            WornByCorpse = item.WornByCorpse,
            Biocoded = item.Biocoded,
            BiocodedPawnLabel = item.BiocodedPawnLabel,
            BiocodedPawnGlobalId = item.BiocodedPawnGlobalId,
            DisplayLabel = item.DisplayLabel,
            MarketValue = item.MarketValue,
            UniqueWeapon = item.UniqueWeapon,
            UniqueWeaponTraits = item.UniqueWeaponTraits.ToList(),
            ThingPackage = item.ThingPackage,
            ThingPackageId = item.ThingPackageId,
            Metadata = item.Metadata.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal)
        };
        ClashOfRimCompatibilityApi.NormalizeThingReferenceMetadata(reference);
        return reference;
    }

    private static void ApplyCorpseMetadata(GiftItemReference item, Corpse corpse)
    {
        if (item.Metadata.TryGetValue(GiftTransporterPayloadUtility.CorpseAgeMetadataKey, out string? ageText)
            && int.TryParse(ageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int age)
            && age >= 0)
        {
            corpse.Age = age;
        }

        if (item.Metadata.TryGetValue(GiftTransporterPayloadUtility.CorpseVanishAfterTimestampMetadataKey, out string? vanishText)
            && int.TryParse(vanishText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vanishAfterTimestamp))
        {
            CorpseVanishAfterTimestampField?.SetValue(corpse, vanishAfterTimestamp);
        }

        if (item.Metadata.TryGetValue(GiftTransporterPayloadUtility.CorpseEverBuriedInSarcophagusMetadataKey, out string? everBuriedText)
            && bool.TryParse(everBuriedText, out bool everBuriedInSarcophagus))
        {
            corpse.everBuriedInSarcophagus = everBuriedInSarcophagus;
        }
    }

    private static void NormalizeGiftPawnForReceiver(Pawn pawn)
    {
        if (pawn.RaceProps?.Animal == true)
        {
            if (pawn.Faction != Faction.OfPlayer)
            {
                pawn.SetFaction(Faction.OfPlayer);
            }

            return;
        }

        if (pawn.guest is null)
        {
            return;
        }

        if (pawn.IsSlave)
        {
            pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave);
            return;
        }

        if (pawn.IsPrisoner)
        {
            if (pawn.Faction == Faction.OfPlayer)
            {
                pawn.SetFaction(null);
            }

            pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
        }
    }

    private static bool MapIdsMatch(string currentMapId, string targetMapId)
    {
        return string.Equals(currentMapId, NormalizeMapLoadId(targetMapId), StringComparison.Ordinal);
    }

    private static string CurrentMapLoadId(Map map)
    {
        return NormalizeMapLoadId(map.uniqueID.ToString());
    }

    private static string NormalizeMapLoadId(string mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        return mapId.StartsWith("Map_", StringComparison.Ordinal)
            ? mapId
            : "Map_" + mapId;
    }

    private static string ResolveLandingMode(string? landingMode)
    {
        if (string.IsNullOrWhiteSpace(landingMode))
        {
            return "CenterNear";
        }

        if (string.Equals(landingMode, "DropPod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(landingMode, "ServerDropPod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(landingMode, "ServerDropPodExchange", StringComparison.OrdinalIgnoreCase))
        {
            return "DropPod";
        }

        if (string.Equals(landingMode, "MapEdge", StringComparison.OrdinalIgnoreCase)
            || string.Equals(landingMode, "Caravan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(landingMode, "SelfDelivery", StringComparison.OrdinalIgnoreCase)
            || string.Equals(landingMode, "CaravanDelivery", StringComparison.OrdinalIgnoreCase))
        {
            return "MapEdge";
        }

        return "CenterNear";
    }

    private static IntVec3 FindDirectDeliveryCell(Map map, string landingMode)
    {
        IntVec3 deliveryPoint = FindCaravanDeliveryPointCell(map);
        if (deliveryPoint.IsValid)
        {
            ClashLog.Message($"[ClashOfRim][GiftLanding] using caravan delivery point cell={deliveryPoint} mode={landingMode}.");
            return deliveryPoint;
        }

        if (landingMode == "MapEdge")
        {
            return FindMapEdgeLandingCell(map);
        }

        return map.Center;
    }

    private static IntVec3 FindCaravanDeliveryPointCell(Map map)
    {
        Building? deliveryPoint = map.listerBuildings.allBuildingsColonist
            .Where(building => building is not null
                && !building.Destroyed
                && building.Spawned
                && string.Equals(building.def?.defName, CaravanDeliveryPointDefName, StringComparison.Ordinal))
            .FirstOrDefault();
        if (deliveryPoint is null)
        {
            return IntVec3.Invalid;
        }

        return deliveryPoint.Position;
    }

    private static IntVec3 FindDropPodLandingCell(Map map, out bool allowRoofPunch)
    {
        if (TryFindOutdoorTradeBeaconDropSpot(map, out IntVec3 beaconDropSpot))
        {
            allowRoofPunch = false;
            ClashLog.Message($"[ClashOfRim][GiftLanding] using outdoor trade beacon drop spot={beaconDropSpot}.");
            return beaconDropSpot;
        }

        IntVec3 centerDropSpot = FindCenterRandomDropSpot(map, out allowRoofPunch);
        ClashLog.Message($"[ClashOfRim][GiftLanding] using center random drop spot={centerDropSpot} allowRoofPunch={allowRoofPunch}.");
        return centerDropSpot;
    }

    private static bool TryFindOutdoorTradeBeaconDropSpot(Map map, out IntVec3 dropSpot)
    {
        foreach (Building beacon in map.listerBuildings.allBuildingsColonist
                     .Where(building => building?.def?.IsOrbitalTradeBeacon == true)
                     .InRandomOrder())
        {
            if (beacon.Position.Roofed(map))
            {
                continue;
            }

            if (DropCellFinder.TryFindDropSpotNear(
                    beacon.Position,
                    map,
                    out dropSpot,
                    allowFogged: false,
                    canRoofPunch: false,
                    allowIndoors: true,
                    size: null,
                    mustBeReachableFromCenter: true)
                && !dropSpot.Roofed(map))
            {
                return true;
            }
        }

        dropSpot = IntVec3.Invalid;
        return false;
    }

    private static IntVec3 FindCenterRandomDropSpot(Map map, out bool allowRoofPunch)
    {
        if (RCellFinder.TryFindRandomCellNearTheCenterOfTheMapWith(
                cell => !cell.Roofed(map)
                    && DropCellFinder.IsGoodDropSpot(cell, map, allowFogged: false, canRoofPunch: false, allowIndoors: false),
                map,
                out IntVec3 result))
        {
            allowRoofPunch = false;
            return result;
        }

        if (CellFinder.TryFindRandomCell(
                map,
                cell => cell.Standable(map)
                    && !cell.Fogged(map)
                    && !cell.Roofed(map),
                out result))
        {
            allowRoofPunch = false;
            return result;
        }

        allowRoofPunch = true;
        result = DropCellFinder.RandomDropSpot(map);
        if (result.IsValid && result.Roofed(map))
        {
            Log.Warning($"[ClashOfRim][GiftLanding] no outdoor drop spot found; fallback may roof punch. cell={result}");
        }
        return result.IsValid ? result : map.Center;
    }

    private static IntVec3 FindMapEdgeLandingCell(Map map)
    {
        bool Validator(IntVec3 cell)
        {
            return cell.Standable(map)
                && !cell.Fogged(map)
                && map.reachability.CanReachColony(cell);
        }

        if (CellFinder.TryFindRandomEdgeCellWith(Validator, map, 0.5f, out IntVec3 result))
        {
            return result;
        }

        if (CellFinder.TryFindRandomEdgeCellWith(cell => cell.Standable(map) && !cell.Fogged(map), map, 0.5f, out result))
        {
            return result;
        }

        return map.Center;
    }

    private static void RollBackPlacedThings(IReadOnlyList<Thing> placedThings)
    {
        foreach (Thing placedThing in placedThings)
        {
            try
            {
                if (!placedThing.Destroyed)
                {
                    placedThing.Destroy(DestroyMode.Vanish);
                }
            }
            catch
            {
            }
        }
    }

    private static void NotifyDirectPlacement(string eventId, Thing thing)
    {
        try
        {
            Messages.Message(
                ClashOfRimText.Key(
                    IsTradeDeliveryEvent(eventId)
                        ? "ClashOfRim.TradeArrivedMessage"
                        : "ClashOfRim.GiftArrivedMessage",
                    thing.LabelCap.ToString().Named("THING"),
                    thing.stackCount.Named("COUNT")),
                new TargetInfo(thing),
                MessageTypeDefOf.PositiveEvent,
                historical: false);
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] notify direct placement failed event={eventId} def={thing.def?.defName ?? "<unknown>"} exception={ex}");
        }
    }

    private static void NotifyDropPodPlacement(string eventId, IntVec3 dropCenter, Map map)
    {
        try
        {
            Messages.Message(
                ClashOfRimText.Key(IsTradeDeliveryEvent(eventId)
                    ? "ClashOfRim.TradeDropPodArrivedMessage"
                    : "ClashOfRim.GiftDropPodArrivedMessage"),
                new TargetInfo(dropCenter, map),
                MessageTypeDefOf.PositiveEvent,
                historical: false);
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] notify drop pod placement failed event={eventId} center={dropCenter} exception={ex}");
        }
    }

    private static bool IsTradeDeliveryEvent(string? eventId)
    {
        string value = eventId ?? string.Empty;
        return value.Length > 0
            && value.IndexOf("trade-completed", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AddArrivalTarget(ICollection<Thing> targets, Thing? thing)
    {
        if (thing is null || thing.Destroyed || targets.Contains(thing))
        {
            return;
        }

        targets.Add(thing);
    }

    private static void NotifyArrivalLetter(GiftLandingPlan plan, IEnumerable<Thing> things)
    {
        if (string.IsNullOrWhiteSpace(plan.ArrivalLetterLabel)
            || string.IsNullOrWhiteSpace(plan.ArrivalLetterText))
        {
            return;
        }

        try
        {
            List<Thing> targets = things
                .Where(thing => thing is not null && !thing.Destroyed)
                .Distinct()
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            Find.LetterStack.ReceiveLetter(
                plan.ArrivalLetterLabel,
                plan.ArrivalLetterText,
                LetterDefOf.PositiveEvent,
                new LookTargets(targets));
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[ClashOfRim][GiftLanding] notify arrival letter failed event={plan.EventId} exception={ex}");
        }
    }
}
