using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed partial class ServerShopRegistry
{
    private readonly Dictionary<string, ServerShopPurchaseReceipt> receipts = new(StringComparer.Ordinal);
    internal object SnapshotCommitGate => sync;

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
