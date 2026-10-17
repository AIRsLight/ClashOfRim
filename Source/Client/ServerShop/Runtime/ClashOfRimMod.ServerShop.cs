using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshServerShop() => _ = shopCatalog.RefreshAsync();

    internal void StartUpsertServerShopListing(string? listingId, string listingKind, ModThingReferenceDto item,
        int priceSilver, int stockCount, double priceIncreaseRatio = 1d,
        string qualityRequirementMode = "AtLeast", string hitPointsRequirementMode = "AtLeast") =>
        _ = shopCatalog.UpsertAsync(new(listingId, listingKind, item, priceSilver, stockCount,
            priceIncreaseRatio, qualityRequirementMode, hitPointsRequirementMode));

    internal void StartRemoveServerShopListing(ModServerShopListingDto listing) => _ = shopCatalog.RemoveAsync(listing.ListingId);

    internal void StartPurchaseServerShopListing(ModServerShopListingDto listing, int purchaseCount)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (shopState.Busy) return;
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            serverShopStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            serverShopStatus = failureReason;
            return;
        }

        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusNoMap");
            Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (listing.Item is null || string.IsNullOrWhiteSpace(listing.ListingId))
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusMissingListing");
            return;
        }

        if (TryStartHydrateServerShopListingPawnPackageForPurchase(listing, purchaseCount))
        {
            return;
        }

        purchaseCount = Math.Max(1, purchaseCount);
        if (listing.StockCount <= 0 || purchaseCount > listing.StockCount)
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusStockNotEnough");
            Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        bool buyFromPlayer = IsServerShopBuyOrder(listing);
        int totalPrice = CalculateServerShopTotalPrice(listing, purchaseCount);
        if (totalPrice >= int.MaxValue)
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusPriceOverflow");
            Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        int availableSilver = buyFromPlayer ? totalPrice : ClientBankRuntime.CountPlayerHomeSilver();
        if (!buyFromPlayer && availableSilver < totalPrice)
        {
            serverShopStatus = ClashOfRimText.Key(
                "ClashOfRim.Shop.StatusInsufficientSilver",
                totalPrice.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        LocalMutationKind operation = LocalMutationKind.Shop;
        BeginLocalAtomicMutation(operation, buyFromPlayer
            ? ClashOfRimText.Key("ClashOfRim.Shop.StatusApplyingSale")
            : ClashOfRimText.Key("ClashOfRim.Shop.StatusApplyingPurchase"));
        string idempotencyKey = $"{(buyFromPlayer ? "shop-sell" : "shop-buy")}:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{listing.ListingId}:{DateTime.UtcNow.Ticks}";
        IReadOnlyList<ModThingReferenceDto> deliveredThings = Array.Empty<ModThingReferenceDto>();
        if (buyFromPlayer)
        {
            if (!TryBuildServerShopSaleThings(map, listing, purchaseCount, out deliveredThings, out IReadOnlyList<ThingRemovalPlan> removalPlans, out string reserveMessage))
            {
                ClearLocalAtomicMutation();
                serverShopStatus = reserveMessage;
                Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            if (totalPrice > 0)
            {
                GiftLandingApplicationResult paymentLanding = GiftLandingApplicator.ApplyToCurrentMap(
                    BuildShopPaymentLandingPlan(listing, purchaseCount, totalPrice, map, idempotencyKey));
                if (!paymentLanding.Success)
                {
                    ClearLocalAtomicMutation();
                    serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusLandingFailed", paymentLanding.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        serverShopStatus,
                        () => StartPurchaseServerShopListing(listing, purchaseCount));
                    return;
                }
            }

            foreach (ThingRemovalPlan plan in removalPlans)
            {
                RemoveShopThingCount(plan.Thing, plan.Count);
            }
        }
        else
        {
            GiftLandingApplicationResult landing = GiftLandingApplicator.ApplyToCurrentMap(BuildShopLandingPlan(listing, purchaseCount, map, idempotencyKey));
            if (!landing.Success)
            {
                ClearLocalAtomicMutation();
                serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusLandingFailed", landing.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    serverShopStatus,
                    () => StartPurchaseServerShopListing(listing, purchaseCount));
                return;
            }

            if (!ClientBankRuntime.TryConsumePlayerHomeSilver(totalPrice, out string consumeMessage))
            {
                serverShopStatus = consumeMessage;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    serverShopStatus,
                    () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
                return;
            }
        }

        StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings);
    }

    private void StartSubmitServerShopPurchase(
        ModServerShopListingDto listing,
        int purchaseCount,
        int totalPrice,
        string idempotencyKey,
        LocalMutationKind operation,
        IReadOnlyList<ModThingReferenceDto> deliveredThings)
    {
        if (!snapshotPreparation.TryBuild(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusSnapshotBuildFailed", buildFailureReason.Named("REASON"));
            ShowUnconfirmedSnapshotFailure(
                operation,
                serverShopStatus,
                () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            ShowUnconfirmedSnapshotFailure(
                operation,
                serverShopStatus,
                () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
            return;
        }

        var request = shopState.Begin();
        if (request is null) { uploadTransaction.Dispose(); return; }
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusUploadingPurchase");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                if (!shopState.TryApply(request, static () => { }, requireBaseSnapshot: true)) return;
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    request.Context);
                ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto> result =
                    await ClientSessionWork.Observe(client.PurchaseServerShopListingWithSnapshotAsync(
                        idempotencyKey,
                        listing.ListingId,
                        NormalizeServerShopListingKind(listing.ListingKind),
                        listing.PriceSilver,
                        totalPrice,
                        purchaseCount,
                        deliveredThings,
                        build.Package!,
                        build.Payload!, request.Scope.Owner.Token));
                using (ClientSessionWork.EnterPublication())
                {
                    if (!shopState.TryApply(request, static () => { }, requireBaseSnapshot: true)) return;
                    if (!result.Success || result.Response is null)
                    {
                        serverShopStatus = ClashOfRimText.Key(
                            "ClashOfRim.Shop.StatusPurchaseFailed",
                            (result.ErrorCode ?? string.Empty).Named("CODE"),
                            (result.Message ?? string.Empty).Named("MESSAGE"));
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            serverShopStatus,
                            () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
                        return;
                    }

                    if (result.Response.Result is not null && !result.Response.Result.Accepted)
                    {
                        serverShopStatus = ClashOfRimText.Key(
                            "ClashOfRim.Shop.StatusPurchaseRejected",
                            result.Response.Result.ErrorCode.Named("CODE"),
                            result.Response.Result.Message.Named("MESSAGE"));
                        ShowUnconfirmedSnapshotFailure(
                            operation,
                            serverShopStatus,
                            () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
                        return;
                    }
                }
                await ClientGamePublication.EnqueueAsync(request.Scope.Game.Enqueue, request.Scope.Owner.Token,
                    action => shopState.TryApply(request, action, requireBaseSnapshot: true), () =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        snapshotPublication.PublishCurrentGame(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    serverShopStatus = ClashOfRimText.Key(
                        IsServerShopBuyOrder(listing)
                            ? "ClashOfRim.Shop.StatusSoldToServer"
                            : "ClashOfRim.Shop.StatusPurchased",
                        purchaseCount.Named("COUNT"),
                        totalPrice.Named("PRICE"));
                    Messages.Message(serverShopStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    uploadTransaction.Dispose();
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                    shopState.End(request);
                    StartRefreshServerShop();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!shopState.TryApply(request, static () => { })) return;
                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.Shop.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    serverShopStatus,
                    () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
                Log.Warning("[ClashOfRim] Server shop purchase failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
                shopState.End(request);
            }
        });
    }

    private static GiftLandingPlan BuildShopLandingPlan(
        ModServerShopListingDto listing,
        int purchaseCount,
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
                    Math.Max(1, item.StackCount) * purchaseCount,
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

    private static GiftLandingPlan BuildShopPaymentLandingPlan(
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
                        qualityRequirementMode: NormalizeServerShopRequirementMode(listing.QualityRequirementMode),
                        hitPointsRequirementMode: NormalizeServerShopRequirementMode(listing.HitPointsRequirementMode))).Named("THING")));
    }

    private bool TryBuildServerShopSaleThings(
        Map map,
        ModServerShopListingDto listing,
        int saleCount,
        out IReadOnlyList<ModThingReferenceDto> deliveredThings,
        out IReadOnlyList<ThingRemovalPlan> removalPlans,
        out string message)
    {
        deliveredThings = Array.Empty<ModThingReferenceDto>();
        removalPlans = Array.Empty<ThingRemovalPlan>();
        if (listing.Item is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Shop.StatusMissingListing");
            return false;
        }

        ModThingReferenceDto requirement = CloneShopRequirement(listing.Item, Math.Max(1, listing.Item.StackCount) * Math.Max(1, saleCount));
        List<ThingCountState> inventory = TradeInventoryUtility.AccessibleMapItems(map, beaconOnly: false)
            .Select(thing => new ThingCountState(thing, Math.Max(1, thing.stackCount)))
            .ToList();

        int remaining = Math.Max(1, requirement.StackCount);
        var plans = new List<ThingRemovalPlan>();
        string qualityRequirementMode = NormalizeServerShopRequirementMode(listing.QualityRequirementMode);
        string hitPointsRequirementMode = NormalizeServerShopRequirementMode(listing.HitPointsRequirementMode);
        foreach (ThingCountState state in inventory
            .Where(state => state.RemainingCount > 0 && TradeThingReferenceUtility.MatchesRequirement(
                requirement,
                state.Thing,
                qualityRequirementMode,
                hitPointsRequirementMode)))
        {
            int take = Math.Min(remaining, state.RemainingCount);
            plans.Add(new ThingRemovalPlan(state.Thing, take));
            state.RemainingCount -= take;
            remaining -= take;
            if (remaining <= 0)
            {
                break;
            }
        }

        if (remaining > 0)
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.Shop.StatusSaleThingsInsufficient",
                TradeUiUtility.ThingLabel(
                    requirement,
                    asRequirement: true,
                    qualityRequirementMode: qualityRequirementMode,
                    hitPointsRequirementMode: hitPointsRequirementMode).Named("THING"));
            return false;
        }

        deliveredThings = plans
            .Select(plan => TradeThingReferenceUtility.BuildThingReference(
                plan.Thing,
                $"owner:{settings.UserId}/colony:{settings.ColonyId}/snapshot:{settings.CurrentSnapshotId}/shop:{listing.ListingId}/thing:{plan.Thing.ThingID}",
                plan.Count,
                BuildShopBiocodedPawnGlobalId(plan.Thing.TryGetComp<CompBiocodable>()?.CodedPawn),
                ThingReferenceSurfaces.ServerShopSale))
            .ToList();

        removalPlans = plans;
        message = string.Empty;
        return true;
    }

    private string? BuildShopBiocodedPawnGlobalId(Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(settings.UserId, pawn);
    }

    private static void RemoveShopThingCount(Thing thing, int count)
    {
        int clamped = Math.Min(Math.Max(0, count), Math.Max(0, thing.stackCount));
        if (clamped <= 0)
        {
            return;
        }

        Thing removed = thing.SplitOff(clamped);
        if (!removed.Destroyed)
        {
            removed.Destroy(DestroyMode.Vanish);
        }
    }

    private static ModThingReferenceDto CloneShopRequirement(ModThingReferenceDto source, int stackCount)
    {
        return new ModThingReferenceDto
        {
            GlobalKey = source.GlobalKey,
            DefName = source.DefName,
            StackCount = Math.Max(1, stackCount),
            Quality = source.Quality,
            HitPoints = source.HitPoints,
            StuffDefName = source.StuffDefName,
            MaxHitPoints = source.MaxHitPoints,
            MinifiedInnerDefName = source.MinifiedInnerDefName,
            MinifiedInnerStuffDefName = source.MinifiedInnerStuffDefName,
            MinifiedInnerQuality = source.MinifiedInnerQuality,
            MinifiedInnerHitPoints = source.MinifiedInnerHitPoints,
            MinifiedInnerMaxHitPoints = source.MinifiedInnerMaxHitPoints,
            WornByCorpse = source.WornByCorpse,
            Biocoded = source.Biocoded,
            BiocodedPawnLabel = source.BiocodedPawnLabel,
            BiocodedPawnGlobalId = source.BiocodedPawnGlobalId,
            DisplayLabel = source.DisplayLabel,
            Metadata = source.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string?>()
        };
    }

    internal static int CalculateServerShopTotalPrice(ModServerShopListingDto listing, int purchaseCount) => ClientShopRules.TotalPrice(listing, purchaseCount);
    internal static int CalculateServerShopUnitPrice(int basePriceSilver, double priceIncreaseRatio, int buyerPurchaseCount) => ServerShopPricing.UnitPrice(basePriceSilver, priceIncreaseRatio, buyerPurchaseCount);
    internal static double NormalizeServerShopPriceIncreaseRatio(double value) => ServerShopPricing.NormalizeRatio(value);
    private static bool IsServerShopBuyOrder(ModServerShopListingDto listing) => ClientShopRules.IsBuyOrder(listing);
    private static string NormalizeServerShopListingKind(string? listingKind) => ClientShopRules.NormalizeListingKind(listingKind);
    private static string NormalizeServerShopRequirementMode(string? mode) => ServerShopQualityRequirementModes.Normalize(mode);

    private bool TryStartHydrateServerShopListingPawnPackageForPurchase(ModServerShopListingDto listing, int purchaseCount)
    {
        if (listing.Item is null || !TradePawnUtility.IsPawnReference(listing.Item)
            || listing.Item.PawnPackage is not null || string.IsNullOrWhiteSpace(listing.Item.PawnPackageId)) return false;
        _ = shopCatalog.HydrateForPurchaseAsync(listing, hydrated => StartPurchaseServerShopListing(hydrated, purchaseCount));
        return true;
    }

    private sealed class ThingCountState
    {
        public ThingCountState(Thing thing, int remainingCount)
        {
            Thing = thing;
            RemainingCount = remainingCount;
        }

        public Thing Thing { get; }

        public int RemainingCount { get; set; }
    }

    private sealed class ThingRemovalPlan
    {
        public ThingRemovalPlan(Thing thing, int count)
        {
            Thing = thing;
            Count = count;
        }

        public Thing Thing { get; }

        public int Count { get; }
    }
}
