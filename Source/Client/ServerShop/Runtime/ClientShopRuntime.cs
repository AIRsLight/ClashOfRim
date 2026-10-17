using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ServerShop;

internal sealed class ClientShopRuntime : IClientShopRuntime
{
    public IClientShopSource Capture() => new ClientShopSource(Find.Maps?.FirstOrDefault(map => map.IsPlayerHome) ?? Find.CurrentMap);
}

internal sealed class ClientShopSource : IClientShopSource
{
    private readonly Map? map;
    internal ClientShopSource(Map? map) => this.map = map;
    public string? Validate(ClientShopCommand command)
    {
        if (map is null || !Find.Maps.Contains(map)) return ClashOfRimText.Key("ClashOfRim.Shop.StatusNoMap");
        if (!ClientShopRules.IsBuyOrder(command.Listing))
        {
            int silver = ClientBankRuntime.CountPlayerHomeSilver();
            if (silver < command.TotalPrice) return ClashOfRimText.Key("ClashOfRim.Shop.StatusInsufficientSilver",
                command.TotalPrice.Named("NEEDED"), silver.Named("AVAILABLE"));
        }
        return null;
    }

    public ClientShopLocalResult Apply(ClashOfRimClientNetworkContext context, ClientShopCommand command, Action mutationStarting)
    {
        var listing = command.Listing;
        IReadOnlyList<ModThingReferenceDto> delivered = Array.Empty<ModThingReferenceDto>();
        if (ClientShopRules.IsBuyOrder(listing))
        {
            if (!TryPlanSale(context, command, out delivered, out var removals, out string failure)) return new(false, delivered, failure);
            var plan = BuildPaymentPlan(listing, command.PurchaseCount, command.TotalPrice, map!, command.Key);
            mutationStarting();
            var payment = GiftLandingApplicator.ApplyToMap(plan, map);
            if (!payment.Success) return FailedLanding(payment);
            foreach (var removal in removals)
            {
                // A compatibility callback may have changed an item while payment
                // was landing. Do not silently clamp an incomplete sale to success.
                if (removal.Thing.Destroyed || removal.Thing.Map != map || removal.Thing.stackCount < removal.Count)
                    return new(false, delivered, MissingThings(listing, listing.Item!));
                TradeMapFulfillmentUtility.RemoveThingCount(removal.Thing, removal.Count);
            }
        }
        else
        {
            var plan = BuildDeliveryPlan(listing, command.PurchaseCount, command.ItemCount, map!, command.Key);
            mutationStarting();
            var landing = GiftLandingApplicator.ApplyToMap(plan, map);
            if (!landing.Success) return FailedLanding(landing);
            if (!ClientBankRuntime.TryConsumePlayerHomeSilver(command.TotalPrice, out string failure)) return new(false, delivered, failure);
        }
        return new(true, delivered, string.Empty);
    }

    private static ClientShopLocalResult FailedLanding(GiftLandingApplicationResult result) => new(false,
        Array.Empty<ModThingReferenceDto>(), ClashOfRimText.Key("ClashOfRim.Shop.StatusLandingFailed", result.Message.Named("MESSAGE")));

    private bool TryPlanSale(ClashOfRimClientNetworkContext context, ClientShopCommand command,
        out IReadOnlyList<ModThingReferenceDto> delivered, out List<TradeOfferSelection> removals, out string message)
    {
        var listing = command.Listing;
        var requirement = ClientPackageCopies.CopyThings(new[] { listing.Item! })[0];
        requirement.StackCount = command.ItemCount;
        removals = new(); delivered = Array.Empty<ModThingReferenceDto>();
        int remaining = command.ItemCount;
        string quality = ServerShopQualityRequirementModes.Normalize(listing.QualityRequirementMode);
        string hitPoints = ServerShopHitPointsRequirementModes.Normalize(listing.HitPointsRequirementMode);
        foreach (var thing in TradeInventoryUtility.AccessibleMapItems(map!, beaconOnly: false))
        {
            if (!TradeThingReferenceUtility.MatchesRequirement(requirement, thing, quality, hitPoints)) continue;
            int take = Math.Min(remaining, Math.Max(0, thing.stackCount));
            if (take == 0) continue;
            removals.Add(new(thing, take)); remaining -= take;
            if (remaining == 0) break;
        }
        if (remaining > 0) { message = MissingThings(listing, requirement); return false; }
        delivered = removals.Select(removal => TradeThingReferenceUtility.BuildThingReference(removal.Thing,
            $"owner:{context.UserId}/colony:{context.ColonyId}/snapshot:{context.CurrentSnapshotId}/shop:{listing.ListingId}/thing:{removal.Thing.ThingID}",
            removal.Count, removal.Thing.TryGetComp<CompBiocodable>()?.CodedPawn is { } pawn && !string.IsNullOrWhiteSpace(pawn.ThingID)
                ? PawnGlobalIdUtility.Build(context.UserId, pawn) : null,
            ThingReferenceSurfaces.ServerShopSale)).ToList();
        message = string.Empty;
        return true;
    }

    private static string MissingThings(ModServerShopListingDto listing, ModThingReferenceDto requirement) =>
        ClashOfRimText.Key("ClashOfRim.Shop.StatusSaleThingsInsufficient", TradeUiUtility.ThingLabel(requirement, asRequirement: true,
            qualityRequirementMode: ServerShopQualityRequirementModes.Normalize(listing.QualityRequirementMode),
            hitPointsRequirementMode: ServerShopHitPointsRequirementModes.Normalize(listing.HitPointsRequirementMode)).Named("THING"));

    private static GiftLandingPlan BuildDeliveryPlan(
        ModServerShopListingDto listing,
        int purchaseCount,
        int itemCount,
        Map map,
        string idempotencyKey)
    {
        ModThingReferenceDto item = listing.Item!;
        ClashOfRimCompatibilityApi.NormalizeThingReferenceMetadata(item);
        return new GiftLandingPlan(
            idempotencyKey,
            worldObjectId: null,
            targetMapUniqueId: "Map_" + map.uniqueID,
            tile: null,
            landingMode: "DropPod",
            new[]
            {
                new GiftItemReference(
                    "shop:" + listing.ListingId + ":" + idempotencyKey,
                    item.DefName,
                    itemCount,
                    sourceSnapshotId: null,
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
                    item.PawnPackage,
                    item.PawnPackageId,
                    thingPackage: item.ThingPackage,
                    thingPackageId: item.ThingPackageId,
                    metadata: item.Metadata)
            },
            requiresSnapshotConfirmation: true,
            arrivalLetterLabel: ClashOfRimText.Key("ClashOfRim.Shop.PurchaseArrivalLetterLabel"),
            arrivalLetterText: ClashOfRimText.Key(
                "ClashOfRim.Shop.PurchaseArrivalLetterText",
                purchaseCount.Named("COUNT"),
                TradeUiUtility.ThingLabel(item, asRequirement: false).Named("THING")));
    }

    private static GiftLandingPlan BuildPaymentPlan(
        ModServerShopListingDto listing,
        int saleCount,
        int totalPrice,
        Map map,
        string idempotencyKey)
    {
        return new GiftLandingPlan(
            idempotencyKey + ":payment",
            worldObjectId: null,
            targetMapUniqueId: "Map_" + map.uniqueID,
            tile: null,
            landingMode: "DropPod",
            new[]
            {
                new GiftItemReference(
                    "shop-payment:" + listing.ListingId + ":" + idempotencyKey,
                    ThingDefOf.Silver.defName,
                    Math.Max(1, totalPrice),
                    sourceSnapshotId: null)
            },
            requiresSnapshotConfirmation: true,
            arrivalLetterLabel: ClashOfRimText.Key("ClashOfRim.Shop.PaymentArrivalLetterLabel"),
            arrivalLetterText: ClashOfRimText.Key(
                "ClashOfRim.Shop.PaymentArrivalLetterText",
                totalPrice.Named("PRICE"),
                saleCount.Named("COUNT"),
                (listing.Item is null
                    ? ClashOfRimText.Key("ClashOfRim.Shop.UnknownItem")
                    : TradeUiUtility.ThingLabel(
                        listing.Item,
                        asRequirement: true,
                        qualityRequirementMode: ServerShopQualityRequirementModes.Normalize(listing.QualityRequirementMode),
                        hitPointsRequirementMode: ServerShopHitPointsRequirementModes.Normalize(listing.HitPointsRequirementMode))).Named("THING")));
    }

}
