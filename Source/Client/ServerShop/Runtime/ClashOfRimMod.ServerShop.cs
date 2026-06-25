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
    internal void StartRefreshServerShop()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            serverShopStatus = failureReason;
            return;
        }

        if (serverShopInProgress)
        {
            return;
        }

        serverShopInProgress = true;
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModListServerShopResponseDto> result =
                    await client.ListServerShopAsync();
                if (!result.Success || result.Response is null)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusRefreshFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusRefreshRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                await HydrateServerShopListingPawnPackagesAsync(client, result.Response.Listings);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    PrepareServerShopPawnPreviews(result.Response.Listings));
                lock (eventStateLock)
                {
                    lastServerShopListings.Clear();
                    lastServerShopListings.AddRange(result.Response.Listings ?? new List<ModServerShopListingDto>());
                    serverShopListingsSnapshotVersion++;
                }

                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.Shop.StatusRefreshed",
                    lastServerShopListings.Count.Named("COUNT"));
            }
            catch (Exception ex)
            {
                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.Shop.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Server shop refresh failed: " + ex);
            }
            finally
            {
                serverShopInProgress = false;
            }
        });
    }

    internal void StartUpsertServerShopListing(
        string? listingId,
        string listingKind,
        ModThingReferenceDto item,
        int priceSilver,
        int stockCount,
        double priceIncreaseRatio = 1d,
        string qualityRequirementMode = "AtLeast",
        string hitPointsRequirementMode = "AtLeast")
    {
        if (!isAdministrator)
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Admin.NotAdministrator");
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            serverShopStatus = failureReason;
            return;
        }

        if (serverShopInProgress)
        {
            return;
        }

        serverShopInProgress = true;
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusSavingListing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto> result =
                    await client.UpsertServerShopListingAsync(
                        listingId,
                        listingKind,
                        item,
                        priceSilver,
                        stockCount,
                        priceIncreaseRatio,
                        NormalizeServerShopRequirementMode(qualityRequirementMode),
                        NormalizeServerShopRequirementMode(hitPointsRequirementMode));
                if (!result.Success || result.Response is null)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusSaveFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusSaveRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Listing is not null)
                {
                    await HydrateServerShopListingPawnPackagesAsync(client, result.Response.Listing);
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        PrepareServerShopPawnPreviews(new[] { result.Response.Listing }));
                }

                lock (eventStateLock)
                {
                    if (result.Response.Listing is not null)
                    {
                        int index = lastServerShopListings.FindIndex(candidate => string.Equals(
                            candidate.ListingId,
                            result.Response.Listing.ListingId,
                            StringComparison.Ordinal));
                        if (index >= 0)
                        {
                            lastServerShopListings[index] = result.Response.Listing;
                        }
                        else
                        {
                            lastServerShopListings.Add(result.Response.Listing);
                        }

                        serverShopListingsSnapshotVersion++;
                    }
                }

                serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusListingSaved");
            }
            catch (Exception ex)
            {
                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.Shop.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Server shop listing save failed: " + ex);
            }
            finally
            {
                serverShopInProgress = false;
            }
        });
    }

    internal void StartRemoveServerShopListing(ModServerShopListingDto listing)
    {
        if (!isAdministrator)
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Admin.NotAdministrator");
            return;
        }

        if (string.IsNullOrWhiteSpace(listing.ListingId))
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusMissingListing");
            return;
        }

        if (serverShopInProgress)
        {
            return;
        }

        serverShopInProgress = true;
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusRemovingListing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto> result =
                    await client.RemoveServerShopListingAsync(listing.ListingId);
                if (!result.Success || result.Response is null)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusRemoveFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    serverShopStatus = ClashOfRimText.Key(
                        "ClashOfRim.Shop.StatusRemoveRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                lock (eventStateLock)
                {
                    int removed = lastServerShopListings.RemoveAll(candidate => string.Equals(
                        candidate.ListingId,
                        listing.ListingId,
                        StringComparison.Ordinal));
                    if (removed > 0)
                    {
                        serverShopListingsSnapshotVersion++;
                    }
                }

                serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusListingRemoved");
            }
            catch (Exception ex)
            {
                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.Shop.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Server shop listing remove failed: " + ex);
            }
            finally
            {
                serverShopInProgress = false;
            }
        });
    }

    internal void StartPurchaseServerShopListing(ModServerShopListingDto listing, int purchaseCount)
    {
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

        int availableSilver = buyFromPlayer ? totalPrice : CountPlayerHomeSilver();
        if (!buyFromPlayer && availableSilver < totalPrice)
        {
            serverShopStatus = ClashOfRimText.Key(
                "ClashOfRim.Shop.StatusInsufficientSilver",
                totalPrice.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            Messages.Message(serverShopStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationShop");
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

            if (!TryConsumePlayerHomeSilver(totalPrice, out string consumeMessage))
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
        string operation,
        IReadOnlyList<ModThingReferenceDto> deliveredThings)
    {
        if (!TryBuildCurrentGameSnapshotPackage(
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

        if (!TryBeginSnapshotUploadTransaction())
        {
            serverShopStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            ShowUnconfirmedSnapshotFailure(
                operation,
                serverShopStatus,
                () => StartSubmitServerShopPurchase(listing, purchaseCount, totalPrice, idempotencyKey, operation, deliveredThings));
            return;
        }

        serverShopInProgress = true;
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusUploadingPurchase");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto> result =
                    await client.PurchaseServerShopListingWithSnapshotAsync(
                        idempotencyKey,
                        listing.ListingId,
                        NormalizeServerShopListingKind(listing.ListingKind),
                        listing.PriceSilver,
                        totalPrice,
                        purchaseCount,
                        deliveredThings,
                        build.Package!,
                        build.Payload!);
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

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
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
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                    StartRefreshServerShop();
                });
            }
            catch (Exception ex)
            {
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
                EndSnapshotUploadTransaction();
                serverShopInProgress = false;
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
                    item.Metadata)
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
                BuildShopBiocodedPawnGlobalId(plan.Thing.TryGetComp<CompBiocodable>()?.CodedPawn)))
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

    internal static int CalculateServerShopTotalPrice(ModServerShopListingDto listing, int purchaseCount)
    {
        int safePurchaseCount = Math.Max(1, purchaseCount);
        long total = 0;
        int basePrice = Math.Max(1, listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver);
        double multiplier = NormalizeServerShopPriceIncreaseRatio(listing.PriceIncreaseRatio);
        int buyerPurchaseCount = Math.Max(0, listing.BuyerPurchaseCount);
        for (int offset = 0; offset < safePurchaseCount; offset++)
        {
            total += CalculateServerShopUnitPrice(basePrice, multiplier, buyerPurchaseCount + offset);
            if (total >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)total;
    }

    internal static int CalculateServerShopUnitPrice(int basePriceSilver, double priceIncreaseRatio, int buyerPurchaseCount)
    {
        int basePrice = Math.Max(1, basePriceSilver);
        if (buyerPurchaseCount <= 0)
        {
            return basePrice;
        }

        double multiplier = NormalizeServerShopPriceIncreaseRatio(priceIncreaseRatio);
        if (Math.Abs(multiplier - 1d) < double.Epsilon)
        {
            return basePrice;
        }

        double calculated = basePrice * Math.Pow(multiplier, buyerPurchaseCount);
        if (double.IsNaN(calculated) || double.IsInfinity(calculated) || calculated >= int.MaxValue)
        {
            return int.MaxValue;
        }

        int rounded = (int)Math.Ceiling(calculated);
        return multiplier > 1d
            ? Math.Max(basePrice, rounded)
            : Math.Max(1, Math.Min(basePrice, rounded));
    }

    internal static double NormalizeServerShopPriceIncreaseRatio(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
        {
            return 1d;
        }

        return Math.Max(0.01d, Math.Min(value, 100d));
    }

    private static bool IsServerShopBuyOrder(ModServerShopListingDto listing)
    {
        return string.Equals(NormalizeServerShopListingKind(listing.ListingKind), "BuyFromPlayer", StringComparison.Ordinal);
    }

    private static string NormalizeServerShopListingKind(string? listingKind)
    {
        return string.Equals(listingKind, "BuyFromPlayer", StringComparison.Ordinal)
            ? "BuyFromPlayer"
            : "SellToPlayer";
    }

    private static string NormalizeServerShopRequirementMode(string? mode)
    {
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? "AtMost"
            : "AtLeast";
    }

    private static async Task HydrateServerShopListingPawnPackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModServerShopListingDto>? listings)
    {
        if (listings is null || listings.Count == 0)
        {
            return;
        }

        foreach (ModServerShopListingDto listing in listings)
        {
            await HydrateServerShopListingPawnPackagesAsync(client, listing);
        }
    }

    private static async Task HydrateServerShopListingPawnPackagesAsync(
        ClashOfRimModNetworkClient client,
        ModServerShopListingDto listing)
    {
        if (listing.Item is null)
        {
            return;
        }

        await PawnPackageTransferService.HydrateThingPawnPackagesAsync(
            client,
            new[] { listing.Item });
    }

    private static void PrepareServerShopPawnPreviews(IReadOnlyList<ModServerShopListingDto>? listings)
    {
        if (listings is null || listings.Count == 0)
        {
            return;
        }

        foreach (ModServerShopListingDto listing in listings)
        {
            if (listing.Item is null)
            {
                continue;
            }

            TradeUiUtility.PreparePawnPreviewsForThingReferences(
                new[] { listing.Item },
                listing.ListingId);
        }
    }

    private bool TryStartHydrateServerShopListingPawnPackageForPurchase(
        ModServerShopListingDto listing,
        int purchaseCount)
    {
        if (listing.Item is null
            || !TradePawnUtility.IsPawnReference(listing.Item)
            || listing.Item.PawnPackage is not null
            || string.IsNullOrWhiteSpace(listing.Item.PawnPackageId))
        {
            return false;
        }

        if (serverShopInProgress)
        {
            return true;
        }

        serverShopInProgress = true;
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                await HydrateServerShopListingPawnPackagesAsync(client, listing);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    PrepareServerShopPawnPreviews(new[] { listing });
                    serverShopInProgress = false;
                    StartPurchaseServerShopListing(listing, purchaseCount);
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                serverShopStatus = ClashOfRimText.Key(
                    "ClashOfRim.PawnPackage.DownloadException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Server shop pawn package hydrate failed before purchase: " + ex);
                serverShopInProgress = false;
            }
        });
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
