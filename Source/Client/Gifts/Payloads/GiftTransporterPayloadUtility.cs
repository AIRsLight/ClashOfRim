using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal static class GiftTransporterPayloadUtility
{
    internal const string CorpseAgeMetadataKey = "ClashOfRim.CorpseAgeTicks";
    internal const string CorpseVanishAfterTimestampMetadataKey = "ClashOfRim.CorpseVanishAfterTimestamp";
    internal const string CorpseEverBuriedInSarcophagusMetadataKey = "ClashOfRim.CorpseEverBuriedInSarcophagus";
    private static readonly FieldInfo? CorpseVanishAfterTimestampField = typeof(Corpse).GetField(
        "vanishAfterTimestamp",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static FloatMenuAcceptanceReport CanSend(
        IEnumerable<IThingHolder> pods,
        string surface = ThingReferenceSurfaces.Gift)
    {
        bool hasPayload = false;
        foreach (Thing thing in EnumerateThings(pods))
        {
            hasPayload = true;
            if (thing is not Pawn
                && !TradeThingReferenceUtility.CanTransferItem(thing, surface, out string? rejectionCode))
            {
                return FloatMenuAcceptanceReport.WithFailReason(ThingTransferPipeline.RejectionMessage(rejectionCode));
            }

            if (thing is Corpse corpse)
            {
                Pawn? innerPawn = corpse.InnerPawn;
                if (innerPawn is null || innerPawn.IsQuestLodger())
                {
                    return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.TransportPodRejectCorpse"));
                }

                continue;
            }

            if (thing is Pawn pawn)
            {
                if (pawn.IsQuestLodger())
                {
                    return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.TransportPodRejectQuestLodger"));
                }

                if (!IsGiftablePawn(pawn))
                {
                    return FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.TransportPodRejectPawn"));
                }
            }
        }

        return hasPayload
            ? FloatMenuAcceptanceReport.WasAccepted
            : FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key("ClashOfRim.GiftDelivery.TransportPodRejectEmpty"));
    }

    public static bool ContainsNonGiftPawn(IEnumerable<IThingHolder> pods)
    {
        return EnumerateThings(pods)
            .Any(thing =>
            {
                if (thing is Corpse corpse)
                {
                    Pawn? innerPawn = corpse.InnerPawn;
                    return innerPawn is null || innerPawn.IsQuestLodger();
                }

                return thing is Pawn pawn && !IsGiftablePawn(pawn);
            });
    }

    public static IReadOnlyList<ModThingReferenceDto> BuildReferences(
        IEnumerable<IThingHolder> pods,
        string userId,
        string colonyId,
        string snapshotId,
        string transporterKey,
        bool forcedDelivery)
    {
        return EnumerateThings(pods)
            .Select(thing => ToThingReference(thing, userId, colonyId, snapshotId, transporterKey, forcedDelivery))
            .ToList();
    }

    private static IEnumerable<Thing> EnumerateThings(IEnumerable<IThingHolder> pods)
    {
        foreach (IThingHolder pod in pods)
        {
            ThingOwner heldThings = pod.GetDirectlyHeldThings();
            for (int index = 0; index < heldThings.Count; index++)
            {
                Thing thing = heldThings[index];
                if (thing is not null && !thing.Destroyed)
                {
                    yield return thing;
                }
            }
        }
    }

    private static ModThingReferenceDto ToThingReference(
        Thing thing,
        string userId,
        string colonyId,
        string snapshotId,
        string transporterKey,
        bool forcedDelivery)
    {
        if (thing is not Pawn)
        {
            CompBiocodable? thingBiocodable = thing.TryGetComp<CompBiocodable>();
            ModThingReferenceDto reference = TradeThingReferenceUtility.BuildThingReference(
                thing,
                $"owner:{userId}/colony:{colonyId}/snapshot:{snapshotId}/transportPods:{transporterKey}/thing:{thing.ThingID}",
                thing.stackCount,
                BuildBiocodedPawnGlobalId(userId, colonyId, snapshotId, thingBiocodable?.CodedPawn),
                forcedDelivery ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift);
            if (thing is Corpse { InnerPawn: not null } corpse)
            {
                reference.PawnPackage = GiftPawnPackageUtility.BuildPackage(
                    corpse.InnerPawn,
                    PawnGlobalIdUtility.Build(userId, corpse.InnerPawn),
                    userId,
                    colonyId,
                    snapshotId,
                    transporterKey);
                reference.Metadata[CorpseAgeMetadataKey] = corpse.Age.ToString(CultureInfo.InvariantCulture);
                reference.Metadata[CorpseVanishAfterTimestampMetadataKey] = ReadCorpseVanishAfterTimestamp(corpse).ToString(CultureInfo.InvariantCulture);
                reference.Metadata[CorpseEverBuriedInSarcophagusMetadataKey] = corpse.everBuriedInSarcophagus.ToString();
            }

            return reference;
        }

        QualityCategory quality;
        string? qualityValue = QualityUtility.TryGetQuality(thing, out quality)
            ? quality.ToString()
            : null;
        Apparel? apparel = thing as Apparel;
        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        CompUniqueWeapon? uniqueWeapon = thing.TryGetComp<CompUniqueWeapon>();

        return new ModThingReferenceDto
        {
            GlobalKey = $"owner:{userId}/colony:{colonyId}/snapshot:{snapshotId}/transportPods:{transporterKey}/thing:{thing.ThingID}",
            DefName = thing.def.defName,
            StackCount = Math.Max(1, thing.stackCount),
            Quality = qualityValue,
            HitPoints = thing.def.useHitPoints ? thing.HitPoints : null,
            WornByCorpse = apparel?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnLabel = biocoded ? biocodable?.CodedPawnLabel : null,
            BiocodedPawnGlobalId = biocoded ? BuildBiocodedPawnGlobalId(userId, colonyId, snapshotId, biocodable?.CodedPawn) : null,
            DisplayLabel = thing.LabelCapNoCount,
            MarketValue = thing.MarketValue,
            UniqueWeapon = uniqueWeapon is null ? null : true,
            UniqueWeaponTraits = uniqueWeapon?.TraitsListForReading
                .Select(trait => trait.defName)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList() ?? new List<string>(),
            PawnPackage = thing is Pawn pawn
                ? GiftPawnPackageUtility.BuildPackage(
                    pawn,
                    PawnGlobalIdUtility.Build(userId, pawn),
                    userId,
                    colonyId,
                    snapshotId,
                    transporterKey)
                : null
        };
    }

    private static int ReadCorpseVanishAfterTimestamp(Corpse corpse)
    {
        return CorpseVanishAfterTimestampField?.GetValue(corpse) is int value ? value : -1;
    }

    private static bool IsGiftablePawn(Pawn pawn)
    {
        if (pawn.IsQuestLodger())
        {
            return false;
        }

        return pawn.RaceProps?.Animal == true
            || pawn.IsPrisoner
            || pawn.IsSlave;
    }

    private static string? BuildBiocodedPawnGlobalId(string userId, string colonyId, string snapshotId, Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(userId, pawn);
    }
}
