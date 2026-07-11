using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

public static class ItemDeliveryClientProcessor
{
    public static ItemDeliveryClientProcessingResult Process(
        ModEventDetailDto detail,
        GiftClientDecision decision,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? rejectionReason = null)
    {
        if (detail is null)
        {
            return ItemDeliveryClientProcessingResult.Failed(
                ItemDeliveryClientProcessingResultKind.NotItemDeliveryEvent,
                ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusDetailMissing"));
        }

        if (!IsGiftDetail(detail))
        {
            return ItemDeliveryClientProcessingResult.Failed(
                ItemDeliveryClientProcessingResultKind.NotItemDeliveryEvent,
                ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusNotItemDeliveryEvent"));
        }

        if (string.IsNullOrWhiteSpace(detail.PayloadSummary))
        {
            return ItemDeliveryClientProcessingResult.Failed(
                ItemDeliveryClientProcessingResultKind.MissingPayload,
                ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusMissingPayload"));
        }

        ItemDeliveryPayloadSummary payload;
        try
        {
            payload = ItemDeliveryPayloadReader.Read(detail.PayloadSummary);
            ClashLog.Message(
                $"[ClashOfRim][GiftProcessor] payload parsed event={detail.EventId} itemCount={payload.Items.Count} message={payload.Message ?? "<null>"}.");
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException)
        {
            Log.Warning(
                $"[ClashOfRim][GiftProcessor] payload parse failed event={detail.EventId} payloadLength={detail.PayloadSummary.Length} exception={ex}");
            return ItemDeliveryClientProcessingResult.Failed(
                ItemDeliveryClientProcessingResultKind.PayloadParseFailed,
                ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusPayloadParseFailed", ex.Message.Named("MESSAGE")));
        }

        if (decision == GiftClientDecision.Reject)
        {
            if (payload.IsForcedDelivery)
            {
                return ItemDeliveryClientProcessingResult.Failed(
                    ItemDeliveryClientProcessingResultKind.NotItemDeliveryEvent,
                    ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusForcedCannotReject"));
            }

            if (string.IsNullOrWhiteSpace(userId)
                || string.IsNullOrWhiteSpace(colonyId)
                || string.IsNullOrWhiteSpace(currentSnapshotId))
            {
                return ItemDeliveryClientProcessingResult.Failed(
                    ItemDeliveryClientProcessingResultKind.MissingIdentity,
                    ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusRejectIdentityMissing"));
            }

            return ItemDeliveryClientProcessingResult.Rejected(new GiftRejectionRequest(
                detail.EventId,
                userId,
                colonyId,
                currentSnapshotId,
                rejectionReason));
        }

        ModEventTargetContextDto? targetContext = detail.TargetContext;
        string? mapUniqueId = targetContext?.MapUniqueId;
        if (string.IsNullOrWhiteSpace(mapUniqueId))
        {
            return ItemDeliveryClientProcessingResult.Failed(
                ItemDeliveryClientProcessingResultKind.MissingTargetMap,
                ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusTargetMapMissing"));
        }

        var items = new List<GiftItemReference>();
        foreach (ItemDeliveryItemSummary item in payload.Items)
        {
            string pawnPackageState = item.PawnPackage is not null
                ? "inline"
                : string.IsNullOrWhiteSpace(item.PawnPackageId)
                    ? "missing"
                    : "id:" + item.PawnPackageId;
            ClashLog.Message(
                $"[ClashOfRim][GiftProcessor] payload item event={detail.EventId} key={item.GlobalKey} def={item.Def ?? "<null>"} count={item.StackCount} sourceSnapshot={item.SourceSnapshotId ?? "<null>"} pawnPackage={pawnPackageState}.");
            items.Add(new GiftItemReference(
                item.GlobalKey,
                item.Def,
                item.StackCount,
                item.SourceSnapshotId,
                item.Quality,
                item.HitPoints,
                item.StuffDefName,
                item.MaxHitPoints,
                item.MinifiedInnerDefName,
                item.MinifiedInnerStuffDefName,
                item.MinifiedInnerQuality,
                item.MinifiedInnerHitPoints,
                item.MinifiedInnerMaxHitPoints,
                item.WornByCorpse,
                item.Biocoded,
                item.BiocodedPawnLabel,
                item.BiocodedPawnGlobalId,
                item.DisplayLabel,
                item.MarketValue,
                item.UniqueWeapon,
                item.UniqueWeaponTraits,
                ToModPawnPackage(item.PawnPackage),
                item.PawnPackageId,
                ToModThingPackage(item.ThingPackage),
                item.ThingPackageId,
                BuildThingReferenceMetadata(item)));
        }

        string? arrivalLetterLabel = null;
        string? arrivalLetterText = null;
        if (payload.IsTradeDelivery)
        {
            arrivalLetterLabel = ClashOfRimText.Key("ClashOfRim.EventLetter.Label.TradeDelivery");
            arrivalLetterText = ClashOfRimText.Key(
                "ClashOfRim.TradeArrivalLetterText",
                FormatPayloadThingList(payload.Items).Named("THINGS"));
        }

        return ItemDeliveryClientProcessingResult.Accepted(new GiftLandingPlan(
            detail.EventId,
            targetContext!.WorldObjectId,
            mapUniqueId!,
            targetContext.Tile,
            targetContext.LandingMode,
            items,
            requiresSnapshotConfirmation: true,
            skipFailedItems: payload.Purpose == ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn,
            arrivalLetterLabel: arrivalLetterLabel,
            arrivalLetterText: arrivalLetterText));
    }

    private static ModPawnExchangePackageDto? ToModPawnPackage(GiftPawnExchangePackageSummary? package)
    {
        if (package is null)
        {
            return null;
        }

        ClashLog.Message(
            $"[ClashOfRim][GiftProcessor] converting pawn package global={package.Reference?.GlobalId ?? "<null>"} thingDef={package.Identity?.ThingDef ?? "<null>"} extensions={package.Extensions.Count} scribeBytes={(package.Scribe?.Xml?.Length ?? 0)}.");
        var result = new ModPawnExchangePackageDto
            {
                PackageVersion = package.PackageVersion,
                Reference = package.Reference is null
                    ? null
                    : new ModCrossMapPawnReferenceDto
                    {
                        GlobalId = package.Reference.GlobalId,
                        SourceSnapshotId = package.Reference.SourceSnapshotId,
                        Name = package.Reference.Name,
                        Dead = package.Reference.Dead,
                        Faction = package.Reference.Faction,
                        Metadata = package.Reference.Metadata
                    },
                Identity = package.Identity is null
                    ? null
                    : new ModPawnExchangeIdentityDto
                    {
                        ThingDef = package.Identity.ThingDef,
                        PawnKindDef = package.Identity.PawnKindDef,
                        FactionDef = package.Identity.FactionDef,
                        Gender = package.Identity.Gender
                    },
                Appearance = package.Appearance is null
                    ? null
                    : new ModPawnExchangeAppearanceDto
                    {
                        DisplayName = package.Appearance.DisplayName,
                        BodyTypeDef = package.Appearance.BodyTypeDef,
                        HeadTypeDef = package.Appearance.HeadTypeDef,
                        HairDef = package.Appearance.HairDef,
                        BeardDef = package.Appearance.BeardDef,
                        SkinColor = package.Appearance.SkinColor,
                        HairColor = package.Appearance.HairColor
                    },
                Status = package.Status is null
                    ? null
                    : new ModPawnExchangeStatusDto
                    {
                        Dead = package.Status.Dead,
                        BiologicalAgeTicks = package.Status.BiologicalAgeTicks,
                        ChronologicalAgeTicks = package.Status.ChronologicalAgeTicks,
                        DeathCauseDef = package.Status.DeathCauseDef,
                        HealthState = package.Status.HealthState
                    },
                Apparel = package.Apparel.Select(ToModEquipmentItem).ToList(),
                Equipment = package.Equipment.Select(ToModEquipmentItem).ToList(),
                Relationships = package.Relationships.Select(relation => new ModPawnExchangeRelationshipStubDto
                {
                    OtherPawnGlobalId = relation.OtherPawnGlobalId,
                    OtherPawnName = relation.OtherPawnName,
                    OtherPawnDead = relation.OtherPawnDead,
                    RelationDef = relation.RelationDef
                }).ToList(),
                Extensions = package.Extensions.Select(extension =>
                    new ModPawnExchangeExtensionPackageDto
                    {
                        ProviderId = extension.ProviderId,
                        Kind = extension.Kind,
                        Metadata = extension.Metadata,
                        PayloadJson = extension.PayloadJson
                    }).ToList(),
                Scribe = package.Scribe is null
                    ? null
                    : new ModPawnScribePayloadDto
                    {
                        Xml = package.Scribe.Xml,
                        XmlSha256 = package.Scribe.XmlSha256,
                        PawnReferenceReplacements = package.Scribe.PawnReferenceReplacements.Select(replacement =>
                            new ModPawnScribePawnReferenceReplacementDto
                            {
                                SourceLoadId = replacement.SourceLoadId,
                                PlaceholderLoadId = replacement.PlaceholderLoadId,
                                Reference = replacement.Reference is null
                                    ? null
                                    : new ModCrossMapPawnReferenceDto
                                    {
                                        GlobalId = replacement.Reference.GlobalId,
                                        SourceSnapshotId = replacement.Reference.SourceSnapshotId,
                                        Name = replacement.Reference.Name,
                                        Dead = replacement.Reference.Dead,
                                        Faction = replacement.Reference.Faction,
                                        Metadata = replacement.Reference.Metadata
                                    }
                            }).ToList()
                    }
            };
        ClashLog.Message(
            $"[ClashOfRim][GiftProcessor] normalizing pawn package global={result.Reference?.GlobalId ?? "<null>"}.");
        ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(result);
        ClashLog.Message(
            $"[ClashOfRim][GiftProcessor] normalized pawn package global={result.Reference?.GlobalId ?? "<null>"} metadataKeys={(result.Reference?.Metadata is null ? string.Empty : string.Join(",", result.Reference.Metadata.Keys))}.");
        return result;
    }

    private static ModThingStatePackageDto? ToModThingPackage(GiftThingStatePackageSummary? package)
    {
        if (package?.Scribe is null)
        {
            return null;
        }

        return new ModThingStatePackageDto
        {
            PackageVersion = package.PackageVersion,
            GlobalKey = package.GlobalKey,
            DefName = package.DefName,
            Label = package.Label,
            StackCount = package.StackCount,
            Fingerprint = package.Fingerprint,
            Scribe = new ModThingScribePayloadDto
            {
                XmlGzipBase64 = package.Scribe.XmlGzipBase64,
                XmlSha256 = package.Scribe.XmlSha256,
                UncompressedBytes = package.Scribe.UncompressedBytes
            }
        };
    }

    private static IReadOnlyDictionary<string, string?> BuildThingReferenceMetadata(ItemDeliveryItemSummary item)
    {
        var reference = new ModThingReferenceDto
        {
            Metadata = item.Metadata is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(item.Metadata, StringComparer.Ordinal)
        };

        ClashOfRimCompatibilityApi.NormalizeThingReferenceMetadata(reference);
        return reference.Metadata;
    }

    private static string FormatPayloadThingList(IReadOnlyCollection<ItemDeliveryItemSummary> items)
    {
        if (items.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.UnknownItem");
        }

        return TradeUiUtility.FormatThingList(
            items.Select(ToThingReferenceForDisplay).ToList(),
            asRequirement: false);
    }

    private static ModThingReferenceDto ToThingReferenceForDisplay(ItemDeliveryItemSummary item)
    {
        return new ModThingReferenceDto
        {
            GlobalKey = item.GlobalKey,
            DefName = item.Def,
            StackCount = item.StackCount,
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
            PawnPackage = ToModPawnPackage(item.PawnPackage),
            PawnPackageId = item.PawnPackageId,
            ThingPackage = ToModThingPackage(item.ThingPackage),
            ThingPackageId = item.ThingPackageId,
            Metadata = BuildThingReferenceMetadata(item).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal)
        };
    }

    private static ModPawnExchangeEquipmentItemDto ToModEquipmentItem(GiftPawnExchangeEquipmentItemSummary item)
    {
        return new ModPawnExchangeEquipmentItemDto
        {
            GlobalId = item.GlobalId,
            Def = item.Def,
            Label = item.Label,
            StackCount = item.StackCount,
            Quality = item.Quality,
            HitPoints = item.HitPoints,
            WornByCorpse = item.WornByCorpse,
            Biocoded = item.Biocoded,
            BiocodedPawnGlobalId = item.BiocodedPawnGlobalId,
            UniqueWeapon = item.UniqueWeapon,
            UniqueWeaponName = item.UniqueWeaponName,
            UniqueWeaponTraits = item.UniqueWeaponTraits
        };
    }

    public static bool IsGiftDetail(ModEventDetailDto detail)
    {
        return detail.EventType == ServerEventType.ItemDelivery
            && detail.PayloadType == EventPayloadType.ItemDelivery;
    }

}
