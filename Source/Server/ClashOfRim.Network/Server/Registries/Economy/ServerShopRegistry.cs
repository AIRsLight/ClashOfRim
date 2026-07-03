using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ServerShopRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object sync = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, ServerShopListingRecord> listings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> buyerPurchaseCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedPurchaseKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<ServerShopListingRecord>? cachedSortedListings;

    public ServerShopRegistry()
    {
    }

    internal ServerShopRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal ServerShopRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public IReadOnlyList<ServerShopListingRecord> List()
    {
        lock (sync)
        {
            cachedSortedListings ??= listings.Values
                .OrderBy(listing => listing.Item.DisplayLabel ?? listing.Item.DefName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
                .ToList();
            return cachedSortedListings;
        }
    }

    public ServerShopListingRecord? Find(string listingId)
    {
        lock (sync)
        {
            return listings.TryGetValue(listingId, out ServerShopListingRecord? listing)
                ? listing
                : null;
        }
    }

    public ServerShopListingRecord Upsert(
        string? listingId,
        string listingKind,
        ThingReferenceDto item,
        int priceSilver,
        int stockCount,
        double priceIncreaseRatio,
        string qualityRequirementMode,
        string hitPointsRequirementMode,
        string actorUserId,
        DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            string id = string.IsNullOrWhiteSpace(listingId)
                ? "shop-" + Guid.NewGuid().ToString("N")
                : listingId.Trim();
            DateTimeOffset createdAt = listings.TryGetValue(id, out ServerShopListingRecord? existing)
                ? existing.CreatedAtUtc
                : nowUtc;
            var record = new ServerShopListingRecord(
                id,
                listingKind,
                item,
                Math.Max(1, priceSilver),
                Math.Max(0, stockCount),
                NormalizePriceIncreaseRatio(priceIncreaseRatio),
                ServerShopQualityRequirementModes.Normalize(qualityRequirementMode),
                ServerShopHitPointsRequirementModes.Normalize(hitPointsRequirementMode),
                createdAt,
                nowUtc,
                actorUserId);
            listings[id] = record;
            InvalidateListingCache();
            Save();
            return record;
        }
    }

    public bool Remove(string listingId)
    {
        lock (sync)
        {
            bool removed = listings.Remove(listingId);
            if (removed)
            {
                RemoveBuyerPurchaseCountsLocked(listingId);
                InvalidateListingCache();
                Save();
            }

            return removed;
        }
    }

    public ServerShopPurchaseResult TryPurchase(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        ProtocolIdentity buyer,
        DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            ServerShopPurchaseResult validation = ValidatePurchaseLocked(
                idempotencyKey,
                listingId,
                listingKind,
                unitPriceSilver,
                totalPriceSilver,
                purchaseCount,
                buyer);
            return CommitPurchaseLocked(validation, idempotencyKey, listingId, purchaseCount, buyer, nowUtc);
        }
    }

    public ServerShopPurchaseResult TryPurchaseAfterCommit(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        ProtocolIdentity buyer,
        DateTimeOffset nowUtc,
        Func<ServerShopPurchaseResult, bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (sync)
        {
            ServerShopPurchaseResult validation = ValidatePurchaseLocked(
                idempotencyKey,
                listingId,
                listingKind,
                unitPriceSilver,
                totalPriceSilver,
                purchaseCount,
                buyer);
            if (!validation.Accepted || validation.Duplicate)
            {
                return validation;
            }

            // The caller performs the save-package commit while this lock is held, so the
            // accepted snapshot and the shop ledger cannot diverge under a concurrent admin edit.
            if (!commit(validation))
            {
                return new ServerShopPurchaseResult(
                    false,
                    false,
                    validation.Listing,
                    validation.RemainingStockCount,
                    "Shop.SnapshotRejected");
            }

            return CommitPurchaseLocked(validation, idempotencyKey, listingId, purchaseCount, buyer, nowUtc);
        }
    }

    public ServerShopPurchaseResult ValidatePurchase(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        ProtocolIdentity buyer)
    {
        lock (sync)
        {
            return ValidatePurchaseLocked(
                idempotencyKey,
                listingId,
                listingKind,
                unitPriceSilver,
                totalPriceSilver,
                purchaseCount,
                buyer);
        }
    }

    private ServerShopPurchaseResult ValidatePurchaseLocked(
        string idempotencyKey,
        string listingId,
        string listingKind,
        int unitPriceSilver,
        int totalPriceSilver,
        int purchaseCount,
        ProtocolIdentity buyer)
    {
        if (completedPurchaseKeys.Contains(idempotencyKey))
        {
            ServerShopListingRecord? duplicateListing = listings.TryGetValue(listingId, out ServerShopListingRecord? duplicate)
                ? duplicate
                : null;
            int duplicateBuyerPurchaseCount = GetBuyerPurchaseCountLocked(listingId, buyer);
            int duplicateUnitPrice = duplicateListing is null
                ? 0
                : CalculateCurrentUnitPrice(duplicateListing, duplicateBuyerPurchaseCount);
            return new ServerShopPurchaseResult(
                true,
                true,
                duplicateListing,
                duplicateListing?.StockCount ?? 0,
                null,
                duplicateUnitPrice,
                duplicateUnitPrice,
                duplicateBuyerPurchaseCount);
        }

        if (!listings.TryGetValue(listingId, out ServerShopListingRecord? listing))
        {
            return new ServerShopPurchaseResult(false, false, null, 0, "Shop.ListingMissing");
        }

        if (!string.Equals(listing.ListingKind, listingKind, StringComparison.Ordinal))
        {
            return new ServerShopPurchaseResult(false, false, listing, listing.StockCount, "Shop.KindChanged");
        }

        if (purchaseCount <= 0)
        {
            return new ServerShopPurchaseResult(false, false, listing, listing.StockCount, "Shop.InvalidPurchaseCount");
        }

        if (listing.StockCount <= 0)
        {
            return new ServerShopPurchaseResult(false, false, listing, listing.StockCount, "Shop.OutOfStock");
        }

        if (purchaseCount > listing.StockCount)
        {
            return new ServerShopPurchaseResult(false, false, listing, listing.StockCount, "Shop.NotEnoughStock");
        }

        int buyerPurchaseCount = GetBuyerPurchaseCountLocked(listingId, buyer);
        int currentUnitPrice = CalculateCurrentUnitPrice(listing, buyerPurchaseCount);
        int expectedTotalPrice = CalculatePurchaseTotalPrice(listing, buyerPurchaseCount, purchaseCount);
        if (unitPriceSilver != currentUnitPrice || totalPriceSilver != expectedTotalPrice)
        {
            return new ServerShopPurchaseResult(
                false,
                false,
                listing,
                listing.StockCount,
                "Shop.PriceChanged",
                currentUnitPrice,
                expectedTotalPrice,
                buyerPurchaseCount);
        }

        return new ServerShopPurchaseResult(
            true,
            false,
            listing,
            listing.StockCount,
            null,
            currentUnitPrice,
            expectedTotalPrice,
            buyerPurchaseCount);
    }

    private ServerShopPurchaseResult CommitPurchaseLocked(
        ServerShopPurchaseResult validation,
        string idempotencyKey,
        string listingId,
        int purchaseCount,
        ProtocolIdentity buyer,
        DateTimeOffset nowUtc)
    {
        if (!validation.Accepted || validation.Duplicate)
        {
            return validation;
        }

        ServerShopListingRecord listing = validation.Listing!;
        ServerShopListingRecord updated = listing with
        {
            StockCount = Math.Max(0, listing.StockCount - purchaseCount),
            UpdatedAtUtc = nowUtc
        };
        listings[listingId] = updated;
        completedPurchaseKeys.Add(idempotencyKey);
        AddBuyerPurchaseCountLocked(listingId, buyer, purchaseCount, updated.ListingKind);
        InvalidateListingCache();
        Save();
        return new ServerShopPurchaseResult(true, false, updated, updated.StockCount, null);
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            Save();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadAll())
        {
            try
            {
                if (pair.Key.StartsWith("listing:", StringComparison.Ordinal))
                {
                    ServerShopListingRecord? listing =
                        JsonSerializer.Deserialize<ServerShopListingRecord>(pair.Value, JsonOptions);
                    if (listing is not null && IsValidLoadedListing(listing))
                    {
                        listings[listing.ListingId] = listing;
                    }
                }
                else if (pair.Key.StartsWith("buyer:", StringComparison.Ordinal))
                {
                    ServerShopBuyerPurchaseRecord? purchase =
                        JsonSerializer.Deserialize<ServerShopBuyerPurchaseRecord>(pair.Value, JsonOptions);
                    if (IsValidBuyerPurchaseRecord(purchase))
                    {
                        buyerPurchaseCounts[BuyerPurchaseKey(purchase!.ListingId, purchase.UserId, purchase.ColonyId)] =
                            purchase.PurchaseCount;
                    }
                }
                else if (pair.Key.StartsWith("completed:", StringComparison.Ordinal))
                {
                    string? key = JsonSerializer.Deserialize<string>(pair.Value, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        completedPurchaseKeys.Add(key);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private bool LoadLegacyReadOnly()
    {
        string? json = legacyPersistence?.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        ServerShopRegistryPersistence? persisted = JsonSerializer.Deserialize<ServerShopRegistryPersistence>(json, JsonOptions);
        if (persisted is null)
        {
            return false;
        }

        bool imported = false;
        foreach (ServerShopListingRecord listing in persisted.Listings ?? Array.Empty<ServerShopListingRecord>())
        {
            if (IsValidLoadedListing(listing))
            {
                if (listings.ContainsKey(listing.ListingId))
                {
                    continue;
                }

                listings[listing.ListingId] = listing;
                imported = true;
            }
            else if (!string.IsNullOrWhiteSpace(listing.ListingId))
            {
                Console.Error.WriteLine("Skipped invalid server shop listing in persisted data: " + listing.ListingId);
            }
        }

        foreach (string key in persisted.CompletedPurchaseKeys ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                imported |= completedPurchaseKeys.Add(key);
            }
        }

        foreach (ServerShopBuyerPurchaseRecord purchase in persisted.BuyerPurchaseCounts ?? Array.Empty<ServerShopBuyerPurchaseRecord>())
        {
            if (IsValidBuyerPurchaseRecord(purchase))
            {
                string key = BuyerPurchaseKey(purchase.ListingId, purchase.UserId, purchase.ColonyId);
                if (!buyerPurchaseCounts.ContainsKey(key))
                {
                    buyerPurchaseCounts[key] = purchase.PurchaseCount;
                    imported = true;
                }
            }
        }

        return imported;
    }

    private void Save()
    {
        if (structuredPersistence is not null)
        {
            Dictionary<string, string> rows = new(StringComparer.Ordinal);
            foreach (ServerShopListingRecord listing in listings.Values.OrderBy(listing => listing.ListingId, StringComparer.Ordinal))
            {
                rows[ListingRowKey(listing.ListingId)] = JsonSerializer.Serialize(listing, JsonOptions);
            }

            foreach (ServerShopBuyerPurchaseRecord purchase in buyerPurchaseCounts
                         .Select(pair => ServerShopBuyerPurchaseRecord.FromKey(pair.Key, pair.Value))
                         .Where(record => record is not null)
                         .Cast<ServerShopBuyerPurchaseRecord>()
                         .OrderBy(record => record.ListingId, StringComparer.Ordinal)
                         .ThenBy(record => record.UserId, StringComparer.Ordinal)
                         .ThenBy(record => record.ColonyId, StringComparer.Ordinal))
            {
                rows[BuyerPurchaseRowKey(purchase.ListingId, purchase.UserId, purchase.ColonyId)] =
                    JsonSerializer.Serialize(purchase, JsonOptions);
            }

            foreach (string key in completedPurchaseKeys.OrderBy(key => key, StringComparer.Ordinal))
            {
                rows[CompletedPurchaseRowKey(key)] = JsonSerializer.Serialize(key, JsonOptions);
            }

            structuredPersistence.ReplaceAll(rows);
            return;
        }

        legacyPersistence?.Write(JsonSerializer.Serialize(
            new ServerShopRegistryPersistence(
                listings.Values.OrderBy(listing => listing.ListingId, StringComparer.Ordinal).ToList(),
                buyerPurchaseCounts
                    .Select(pair => ServerShopBuyerPurchaseRecord.FromKey(pair.Key, pair.Value))
                    .Where(record => record is not null)
                    .Cast<ServerShopBuyerPurchaseRecord>()
                    .OrderBy(record => record.ListingId, StringComparer.Ordinal)
                    .ThenBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
                    .ToList(),
                completedPurchaseKeys.OrderBy(key => key, StringComparer.Ordinal).ToList()),
            JsonOptions));
    }

    private void InvalidateListingCache()
    {
        cachedSortedListings = null;
    }

    private static bool IsValidLoadedListing(ServerShopListingRecord listing)
    {
        return !string.IsNullOrWhiteSpace(listing.ListingId)
            && ServerShopListingKinds.IsKnown(listing.ListingKind)
            && ServerShopQualityRequirementModes.IsKnown(listing.QualityRequirementMode)
            && ServerShopHitPointsRequirementModes.IsKnown(listing.HitPointsRequirementMode)
            && listing.Item is not null;
    }

    private static bool IsValidBuyerPurchaseRecord(ServerShopBuyerPurchaseRecord? purchase)
    {
        return purchase is not null
            && !string.IsNullOrWhiteSpace(purchase.ListingId)
            && !string.IsNullOrWhiteSpace(purchase.UserId)
            && !string.IsNullOrWhiteSpace(purchase.ColonyId)
            && purchase.PurchaseCount > 0;
    }

    public int GetBuyerPurchaseCount(string listingId, ProtocolIdentity? buyer)
    {
        lock (sync)
        {
            return GetBuyerPurchaseCountLocked(listingId, buyer);
        }
    }

    public static int CalculateCurrentUnitPrice(ServerShopListingRecord listing, int buyerPurchaseCount)
    {
        return CalculateSteppedPrice(listing.PriceSilver, listing.PriceIncreaseRatio, buyerPurchaseCount);
    }

    public static int CalculatePurchaseTotalPrice(ServerShopListingRecord listing, int buyerPurchaseCount, int purchaseCount)
    {
        int safePurchaseCount = Math.Max(1, purchaseCount);
        long total = 0;
        for (int offset = 0; offset < safePurchaseCount; offset++)
        {
            total += CalculateSteppedPrice(listing.PriceSilver, listing.PriceIncreaseRatio, buyerPurchaseCount + offset);
            if (total >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)total;
    }

    private static int CalculateSteppedPrice(int basePriceSilver, double priceIncreaseRatio, int buyerPurchaseCount)
    {
        int basePrice = Math.Max(1, basePriceSilver);
        if (buyerPurchaseCount <= 0)
        {
            return basePrice;
        }

        double multiplier = NormalizePriceIncreaseRatio(priceIncreaseRatio);
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

    private static double NormalizePriceIncreaseRatio(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
        {
            return 1d;
        }

        return Math.Clamp(value, 0.01d, 100d);
    }

    private int GetBuyerPurchaseCountLocked(string listingId, ProtocolIdentity? buyer)
    {
        if (string.IsNullOrWhiteSpace(listingId)
            || string.IsNullOrWhiteSpace(buyer?.UserId)
            || string.IsNullOrWhiteSpace(buyer.ColonyId))
        {
            return 0;
        }

        return buyerPurchaseCounts.TryGetValue(BuyerPurchaseKey(listingId, buyer.UserId, buyer.ColonyId!), out int count)
            ? Math.Max(0, count)
            : 0;
    }

    private void AddBuyerPurchaseCountLocked(string listingId, ProtocolIdentity buyer, int purchaseCount, string listingKind)
    {
        if (!ServerShopListingKinds.IsKnown(listingKind)
            || string.IsNullOrWhiteSpace(listingId)
            || string.IsNullOrWhiteSpace(buyer.UserId)
            || string.IsNullOrWhiteSpace(buyer.ColonyId)
            || purchaseCount <= 0)
        {
            return;
        }

        string key = BuyerPurchaseKey(listingId, buyer.UserId, buyer.ColonyId!);
        buyerPurchaseCounts[key] = buyerPurchaseCounts.TryGetValue(key, out int existing)
            ? Math.Max(0, existing) + purchaseCount
            : purchaseCount;
    }

    private void RemoveBuyerPurchaseCountsLocked(string listingId)
    {
        string prefix = listingId + "\u001f";
        foreach (string key in buyerPurchaseCounts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            buyerPurchaseCounts.Remove(key);
        }
    }

    private static string BuyerPurchaseKey(string listingId, string userId, string colonyId)
    {
        return listingId + "\u001f" + userId + "\u001f" + colonyId;
    }

    private static string ListingRowKey(string listingId)
    {
        return "listing:" + listingId;
    }

    private static string BuyerPurchaseRowKey(string listingId, string userId, string colonyId)
    {
        return "buyer:" + BuyerPurchaseKey(listingId, userId, colonyId);
    }

    private static string CompletedPurchaseRowKey(string idempotencyKey)
    {
        return "completed:" + idempotencyKey;
    }

    private sealed record ServerShopRegistryPersistence(
        IReadOnlyList<ServerShopListingRecord> Listings,
        IReadOnlyList<ServerShopBuyerPurchaseRecord> BuyerPurchaseCounts,
        IReadOnlyList<string> CompletedPurchaseKeys);
}

public sealed record ServerShopListingRecord(
    string ListingId,
    string ListingKind,
    ThingReferenceDto Item,
    int PriceSilver,
    int StockCount,
    double PriceIncreaseRatio,
    string QualityRequirementMode,
    string HitPointsRequirementMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedByUserId);

public sealed record ServerShopBuyerPurchaseRecord(
    string ListingId,
    string UserId,
    string ColonyId,
    int PurchaseCount)
{
    public static ServerShopBuyerPurchaseRecord? FromKey(string key, int purchaseCount)
    {
        string[] parts = key.Split('\u001f');
        return parts.Length == 3
            ? new ServerShopBuyerPurchaseRecord(parts[0], parts[1], parts[2], Math.Max(0, purchaseCount))
            : null;
    }
}

public sealed record ServerShopPurchaseResult(
    bool Accepted,
    bool Duplicate,
    ServerShopListingRecord? Listing,
    int RemainingStockCount,
    string? FailureKey,
    int CurrentUnitPriceSilver = 0,
    int ExpectedTotalPriceSilver = 0,
    int BuyerPurchaseCount = 0);
