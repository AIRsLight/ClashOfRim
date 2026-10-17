using AIRsLight.ClashOfRim.Save;
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
    private readonly Dictionary<string, ServerShopPurchaseReceipt> receipts = new(StringComparer.Ordinal);
    private IReadOnlyList<ServerShopListingRecord>? cachedSortedListings;
    internal object SnapshotCommitGate => sync;

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
            PersistShopChanges(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ListingRowKey(record.ListingId)] = JsonSerializer.Serialize(record, JsonOptions)
                },
                Array.Empty<string>());
            return record;
        }
    }

    public bool Remove(string listingId)
    {
        lock (sync)
        {
            string buyerPrefix = listingId + "\u001f";
            string[] buyerRows = buyerPurchaseCounts.Keys
                .Where(key => key.StartsWith(buyerPrefix, StringComparison.Ordinal))
                .Select(key => "buyer:" + key)
                .ToArray();
            bool removed = listings.ContainsKey(listingId);
            if (removed)
            {
                PersistShopChanges(
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    [ListingRowKey(listingId), .. buyerRows]);
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

            // This only excludes concurrent edits. A durable snapshot intent is required
            // to recover interruptions between the callback and the shop write.
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
        var purchase = new ServerShopBuyerPurchaseRecord(listingId, buyer.UserId, buyer.ColonyId!,
            checked(validation.BuyerPurchaseCount + purchaseCount));
        PersistShopChanges(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ListingRowKey(updated.ListingId)] = JsonSerializer.Serialize(updated, JsonOptions),
                [BuyerPurchaseRowKey(purchase.ListingId, purchase.UserId, purchase.ColonyId)] =
                    JsonSerializer.Serialize(purchase, JsonOptions),
                [CompletedPurchaseRowKey(idempotencyKey)] = JsonSerializer.Serialize(idempotencyKey, JsonOptions)
            },
            Array.Empty<string>());
        return new ServerShopPurchaseResult(true, false, updated, updated.StockCount, null);
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAllForImport(BuildStructuredRows());
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        LoadRows(structuredPersistence.ReadAll());
    }

    private void LoadRows(IReadOnlyDictionary<string, string> rows)
    {
        foreach (KeyValuePair<string, string> pair in rows)
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
                else if (pair.Key.StartsWith("receipt:", StringComparison.Ordinal))
                {
                    var receipt = JsonSerializer.Deserialize<ServerShopPurchaseReceipt>(pair.Value, JsonOptions);
                    if (receipt is not null) receipts[receipt.IdempotencyKey] = receipt;
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

        foreach (var receipt in persisted.Receipts ?? Array.Empty<ServerShopPurchaseReceipt>())
            imported |= receipts.TryAdd(receipt.IdempotencyKey, receipt);
        return imported;
    }

    private void SaveCandidate(IReadOnlyDictionary<string, string> upserts, IReadOnlyCollection<string> deletes)
    {
        if (legacyPersistence is null) return;
        Dictionary<string, string> rows = BuildStructuredRows();
        foreach (string key in deletes) rows.Remove(key);
        foreach (var pair in upserts) rows[pair.Key] = pair.Value;
        var candidate = new ServerShopRegistry();
        candidate.LoadRows(rows);
        legacyPersistence.Write(JsonSerializer.Serialize(
            new ServerShopRegistryPersistence(
                candidate.listings.Values.OrderBy(listing => listing.ListingId, StringComparer.Ordinal).ToList(),
                candidate.buyerPurchaseCounts
                    .Select(pair => ServerShopBuyerPurchaseRecord.FromKey(pair.Key, pair.Value))
                    .Where(record => record is not null)
                    .Cast<ServerShopBuyerPurchaseRecord>()
                    .OrderBy(record => record.ListingId, StringComparer.Ordinal)
                    .ThenBy(record => record.UserId, StringComparer.Ordinal)
                    .ThenBy(record => record.ColonyId, StringComparer.Ordinal)
                    .ToList(),
                candidate.completedPurchaseKeys.OrderBy(key => key, StringComparer.Ordinal).ToList(),
                candidate.receipts.Values.OrderBy(receipt => receipt.IdempotencyKey, StringComparer.Ordinal).ToList()),
            JsonOptions));
    }

    private Dictionary<string, string> BuildStructuredRows()
    {
        Dictionary<string, string> rows = new(StringComparer.Ordinal);
        foreach (ServerShopListingRecord listing in listings.Values)
        {
            rows[ListingRowKey(listing.ListingId)] = JsonSerializer.Serialize(listing, JsonOptions);
        }

        foreach (ServerShopBuyerPurchaseRecord purchase in buyerPurchaseCounts
                     .Select(pair => ServerShopBuyerPurchaseRecord.FromKey(pair.Key, pair.Value))
                     .Where(record => record is not null)
                     .Cast<ServerShopBuyerPurchaseRecord>())
        {
            rows[BuyerPurchaseRowKey(purchase.ListingId, purchase.UserId, purchase.ColonyId)] =
                JsonSerializer.Serialize(purchase, JsonOptions);
        }

        foreach (string key in completedPurchaseKeys)
        {
            rows[CompletedPurchaseRowKey(key)] = JsonSerializer.Serialize(key, JsonOptions);
        }
        foreach (var receipt in receipts.Values)
            rows["receipt:" + receipt.IdempotencyKey] = JsonSerializer.Serialize(receipt, JsonOptions);

        return rows;
    }

    private void PersistShopChanges(
        IReadOnlyDictionary<string, string> upserts,
        IReadOnlyCollection<string> deletes)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ApplyBatch(upserts, deletes);
        }
        else SaveCandidate(upserts, deletes);

        // Only publish changes after the complete durable batch has succeeded.
        foreach (string key in deletes)
        {
            if (key.StartsWith("listing:", StringComparison.Ordinal)) listings.Remove(key[8..]);
            else if (key.StartsWith("buyer:", StringComparison.Ordinal)) buyerPurchaseCounts.Remove(key[6..]);
            else if (key.StartsWith("completed:", StringComparison.Ordinal)) completedPurchaseKeys.Remove(key[10..]);
            else if (key.StartsWith("receipt:", StringComparison.Ordinal)) receipts.Remove(key[8..]);
        }
        LoadRows(upserts);
        InvalidateListingCache();
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

    public static int CalculateCurrentUnitPrice(ServerShopListingRecord listing, int buyerPurchaseCount) =>
        ServerShopPricing.UnitPrice(listing.PriceSilver, listing.PriceIncreaseRatio, buyerPurchaseCount);
    public static int CalculatePurchaseTotalPrice(ServerShopListingRecord listing, int buyerPurchaseCount, int purchaseCount) =>
        ServerShopPricing.TotalPrice(listing.PriceSilver, listing.PriceIncreaseRatio, buyerPurchaseCount, purchaseCount);
    private static double NormalizePriceIncreaseRatio(double value) => ServerShopPricing.NormalizeRatio(value);

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
        IReadOnlyList<string> CompletedPurchaseKeys,
        IReadOnlyList<ServerShopPurchaseReceipt>? Receipts = null);

    public ServerShopPurchaseReceipt? FindReceipt(string idempotencyKey)
    {
        lock (sync) return receipts.GetValueOrDefault(idempotencyKey);
    }

    internal ShopSnapshotMutation PrepareSnapshotCommit(PurchaseServerShopListingRequest request,
        LatestSnapshotRecord snapshot)
    {
        lock (sync)
        {
            var validation = ValidatePurchaseLocked(request.IdempotencyKey, request.ListingId, request.ListingKind,
                request.UnitPriceSilver, request.TotalPriceSilver, request.PurchaseCount, request.Buyer);
            if (!validation.Accepted || validation.Duplicate)
                throw new InvalidOperationException("Shop purchase must be validated before preparing a commit.");
            var listing = validation.Listing!;
            var updated = listing with { StockCount = listing.StockCount - request.PurchaseCount, UpdatedAtUtc = snapshot.AcceptedAtUtc };
            var receipt = new ServerShopPurchaseReceipt(request.IdempotencyKey, request.Buyer.UserId, request.Buyer.ColonyId!,
                snapshot.Identity.SnapshotId!, request.ListingId, request.ListingKind, request.PurchaseCount,
                request.UnitPriceSilver, request.TotalPriceSilver, updated.StockCount,
                checked(validation.BuyerPurchaseCount + request.PurchaseCount), snapshot.AcceptedAtUtc);
            var mutation = new ShopSnapshotMutation(listing, updated, validation.BuyerPurchaseCount, receipt);
            mutation.Validate(snapshot.Identity);
            return mutation;
        }
    }

    internal void ValidateSnapshotCommit(ShopSnapshotMutation mutation)
    {
        lock (sync)
        {
            var receipt = mutation.Receipt;
            if (receipts.TryGetValue(receipt.IdempotencyKey, out var existing))
            {
                if (existing != receipt) throw new InvalidDataException("Shop receipt conflicts with the pending commit.");
                return; // Later admin edits must not be reverted when a completed purchase is replayed.
            }
            if (completedPurchaseKeys.Contains(receipt.IdempotencyKey)
                || !listings.TryGetValue(receipt.ListingId, out var listing)
                || JsonSerializer.Serialize(listing, JsonOptions) != JsonSerializer.Serialize(mutation.PreviousListing, JsonOptions)
                || GetBuyerPurchaseCountLocked(receipt.ListingId, new ProtocolIdentity(receipt.UserId, receipt.ColonyId, null)) != mutation.PreviousBuyerPurchaseCount)
                throw new InvalidDataException("Shop stock or purchase history conflicts with the pending commit.");
        }
    }

    internal void ApplySnapshotCommit(ShopSnapshotMutation mutation)
    {
        lock (sync)
        {
            ValidateSnapshotCommit(mutation);
            var receipt = mutation.Receipt;
            if (receipts.ContainsKey(receipt.IdempotencyKey)) return;
            var purchase = new ServerShopBuyerPurchaseRecord(receipt.ListingId, receipt.UserId, receipt.ColonyId, receipt.BuyerPurchaseCount);
            PersistShopChanges(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ListingRowKey(receipt.ListingId)] = JsonSerializer.Serialize(mutation.UpdatedListing, JsonOptions),
                [BuyerPurchaseRowKey(receipt.ListingId, receipt.UserId, receipt.ColonyId)] = JsonSerializer.Serialize(purchase, JsonOptions),
                [CompletedPurchaseRowKey(receipt.IdempotencyKey)] = JsonSerializer.Serialize(receipt.IdempotencyKey, JsonOptions),
                ["receipt:" + receipt.IdempotencyKey] = JsonSerializer.Serialize(receipt, JsonOptions)
            }, Array.Empty<string>());
        }
    }
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

public sealed record ServerShopPurchaseReceipt(string IdempotencyKey, string UserId, string ColonyId, string SnapshotId,
    string ListingId, string ListingKind, int PurchaseCount, int UnitPriceSilver, int TotalPriceSilver,
    int RemainingStockCount, int BuyerPurchaseCount, DateTimeOffset AcceptedAtUtc)
{
    public bool Matches(PurchaseServerShopListingRequest request, string snapshotId) =>
        IdempotencyKey == request.IdempotencyKey && UserId == request.Buyer.UserId && ColonyId == request.Buyer.ColonyId
        && SnapshotId == snapshotId && ListingId == request.ListingId && ListingKind == request.ListingKind
        && PurchaseCount == request.PurchaseCount && UnitPriceSilver == request.UnitPriceSilver && TotalPriceSilver == request.TotalPriceSilver;
}

public sealed record ShopSnapshotMutation(ServerShopListingRecord PreviousListing, ServerShopListingRecord UpdatedListing,
    int PreviousBuyerPurchaseCount, ServerShopPurchaseReceipt Receipt)
{
    internal void Validate(SnapshotIdentity identity)
    {
        if (Receipt.UserId != identity.OwnerId || Receipt.ColonyId != identity.ColonyId || Receipt.SnapshotId != identity.SnapshotId
            || string.IsNullOrWhiteSpace(Receipt.IdempotencyKey) || Receipt.PurchaseCount <= 0 || PreviousBuyerPurchaseCount < 0
            || Receipt.ListingId != PreviousListing.ListingId || Receipt.ListingId != UpdatedListing.ListingId
            || Receipt.ListingKind != PreviousListing.ListingKind || Receipt.ListingKind != UpdatedListing.ListingKind
            || (long)PreviousListing.StockCount - Receipt.PurchaseCount != UpdatedListing.StockCount || UpdatedListing.StockCount < 0
            || Receipt.RemainingStockCount != UpdatedListing.StockCount
            || (long)PreviousBuyerPurchaseCount + Receipt.PurchaseCount != Receipt.BuyerPurchaseCount
            || Receipt.UnitPriceSilver != ServerShopRegistry.CalculateCurrentUnitPrice(PreviousListing, PreviousBuyerPurchaseCount)
            || Receipt.TotalPriceSilver != ServerShopRegistry.CalculatePurchaseTotalPrice(PreviousListing, PreviousBuyerPurchaseCount, Receipt.PurchaseCount))
            throw new InvalidDataException("Shop journal does not match the confirmed purchase.");
    }
}
