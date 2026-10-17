using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Shop;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class ShopModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (string kind in new[] { ServerShopListingKinds.SellToPlayer, ServerShopListingKinds.BuyFromPlayer })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                PurchaseAndReplay(factory, kind);
                foreach (int phase in new[] { 1, 2 }) Revoked(factory, kind, phase);
            }
            foreach (string change in new[] { "price", "stock", "kind", "item", "quality", "hitpoints", "remove" })
                ChangedListing(factory, change);
            foreach (string mismatch in new[] { "quantity", "quality", "hitpoints", "item", "pawn", "thing-package", "overflow" }) InvalidDelivery(factory, mismatch);
            MaximumQuantity(factory);
            Administration(factory);
            CopyComplexItem();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void PurchaseAndReplay(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        var f = new EconomicModuleTestContext(factory); Seed(f, kind);
        var command = Command(f, kind);
        foreach (string? token in new[] { null, "forged", f.Issue("other", "other-colony") })
        {
            string before = Evidence(f);
            Check(!f.Shop.Purchase(command, f.Package.Payload, token).Result.Accepted && f.Recovery.Replays == 0 && Evidence(f) == before,
                "Unauthorized purchase ran recovery or changed the aggregate.");
        }
        var first = f.Shop.Purchase(command, f.Package.Payload, f.Token);
        Check(first.Result.Accepted && first.AppliedSnapshotId == "paid" && first.RemainingStockCount == 3
            && f.ShopRegistry.GetBuyerPurchaseCount("listing", Buyer) == 2 && f.Recovery.Commits == 1, "Independent shop commit failed: " + first.Result.Message);
        var listed = f.Shop.List(new("userA", "colonyA", f.Token)).Listings.Single();
        Check(listed.BuyerPurchaseCount == 2 && listed.StockCount == 3, "Listing lost the confirmed buyer count or stock.");
        // Admin removal must not erase the authoritative purchase receipt.
        f.ShopRegistry.Remove("listing"); string evidence = Evidence(f);
        var replay = f.Shop.Purchase(command, f.Package.Payload, f.Token);
        Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Result.Accepted
            && replay.AppliedSnapshotId == first.AppliedSnapshotId && replay.NextLineageToken == first.NextLineageToken
            && replay.RemainingStockCount == first.RemainingStockCount && Evidence(f) == evidence && f.Recovery.Commits == 1,
            "Replay rebuilt a listing or repeated the committed effects.");
        var other = new ProtocolIdentity("other", "other-colony", "before");
        Check(f.Shop.Purchase(Command(f, kind, other), f.Package.Payload, f.Issue(other.UserId, other.ColonyId!)).Result.ErrorCode == ProtocolErrorCode.Conflict,
            "Foreign buyer borrowed a committed receipt.");
        Check(f.Shop.Purchase(Command(f, kind, price: 11), f.Package.Payload, f.Token).Result.ErrorCode == ProtocolErrorCode.Conflict
            && Evidence(f) == evidence, "Changed command reused a committed receipt.");
        f.Tokens.RevokeForUser("userA");
        Check(!f.Shop.Purchase(command, f.Package.Payload, f.Token).Result.Accepted && Evidence(f) == evidence, "Replay bypassed revocation.");
    }

    private static void Revoked(Func<string, SaveSnapshotPackage> factory, string kind, int phase)
    {
        var f = new EconomicModuleTestContext(factory); Seed(f, kind); string before = Evidence(f); int calls = 0;
        f.Recovery.OnReplay = () => { if (++calls == phase) f.Tokens.RevokeForUser("userA"); };
        Check(!f.Shop.Purchase(Command(f, kind), f.Package.Payload, f.Token).Result.Accepted && calls == phase
            && Evidence(f) == before && f.Recovery.Commits == 0, "Recovery revocation did not prevent shop commit at phase " + phase);
    }

    private static void ChangedListing(Func<string, SaveSnapshotPackage> factory, string change)
    {
        var f = new EconomicModuleTestContext(factory); Seed(f, ServerShopListingKinds.BuyFromPlayer);
        string? changed = null; int calls = 0;
        f.Recovery.OnReplay = () =>
        {
            if (++calls != 2) return;
            if (change == "remove") f.ShopRegistry.Remove("listing");
            else Seed(f, change == "kind" ? ServerShopListingKinds.SellToPlayer : ServerShopListingKinds.BuyFromPlayer,
                change == "price" ? 11 : 10, change == "stock" ? 1 : 5,
                new("requirement", change == "item" ? "WoodLog" : "Steel", 2,
                    quality: change == "quality" ? "Legendary" : null, hitPoints: change == "hitpoints" ? 100 : null, maxHitPoints: 100));
            changed = Evidence(f);
        };
        var request = Command(f, ServerShopListingKinds.BuyFromPlayer,
            delivered: new[] { new ThingReferenceDto("steel", "Steel", 4, quality: "Normal", hitPoints: 50, maxHitPoints: 100) });
        Check(!f.Shop.Purchase(request, f.Package.Payload, f.Token).Result.Accepted && calls == 2
            && changed == Evidence(f) && f.Recovery.Commits == 0, "Final purchase ignored a listing change: " + change);
    }

    private static void InvalidDelivery(Func<string, SaveSnapshotPackage> factory, string mismatch)
    {
        var f = new EconomicModuleTestContext(factory);
        Seed(f, ServerShopListingKinds.BuyFromPlayer, item: new("requirement", "Steel", mismatch == "overflow" ? int.MaxValue : 2, quality: "Normal", hitPoints: 50, maxHitPoints: 100));
        var delivered = new ThingReferenceDto("steel", mismatch == "item" ? "WoodLog" : "Steel", mismatch == "overflow" ? 1 : mismatch == "quantity" ? 3 : 4,
            quality: mismatch == "quality" ? "Awful" : "Normal", hitPoints: mismatch == "hitpoints" ? 10 : 50, maxHitPoints: 100,
            pawnPackage: mismatch == "pawn" ? new PawnExchangePackageDto(0, null!, null!, null!, null!, [], [], []) : null,
            thingPackage: mismatch == "thing-package" ? new ThingStatePackageDto(0, "", "Steel", null, 0, new("", null, 0)) : null);
        string before = Evidence(f);
        var result = f.Shop.Purchase(Command(f, ServerShopListingKinds.BuyFromPlayer, delivered: new[] { delivered }), f.Package.Payload, f.Token);
        Check(!result.Result.Accepted && f.Recovery.Replays == 1 && Evidence(f) == before, "Invalid delivery reached snapshot commit: " + mismatch);
    }

    private static void Administration(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new EconomicModuleTestContext(factory);
        var item = new ThingReferenceDto("steel", "Steel", 2, marketValue: 99);
        UpsertServerShopListingRequest Request(string? token) => new("userA", "colonyA", "listing", ServerShopListingKinds.SellToPlayer, item, 10, 5, priceIncreaseRatio: 1.5, authToken: token);
        Check(!f.Shop.Upsert(Request(f.Token)).Result.Accepted && f.Recovery.Replays == 0, "Non-administrator changed shop or started recovery.");
        f.World.PromoteAdministrator("root"); f.World.PromoteAdministrator("userA");
        f.Baseline.Submit(new(ProtocolApiVersion.Current, "root", "root-colony", DateTimeOffset.UtcNow, [new("WoodLog", 1)], []));
        Check(!f.Shop.Upsert(Request(f.Token)).Result.Accepted && f.ShopRegistry.List().Count == 0, "Shop ignored authoritative definitions.");
        f.Baseline.Submit(new(ProtocolApiVersion.Current, "root", "root-colony", DateTimeOffset.UtcNow, [new("Steel", 1)], []));
        var created = f.Shop.Upsert(Request(f.Token));
        Check(created.Result.Accepted && created.Listing!.Item.MarketValue is null && created.Listing.PriceIncreaseRatio == 1.5, "Listing normalization changed.");
        Check(!f.Shop.List(new("userA", "colonyA", "forged")).Result.Accepted, "List accepted forged identity.");
        var receipt = f.Shop.Purchase(Command(f, ServerShopListingKinds.SellToPlayer, count: 1), f.Package.Payload, f.Token);
        Check(receipt.Result.Accepted && f.Shop.List(new("userA", "colonyA", f.Token)).Listings.Single().PriceSilver == 15
            && f.Shop.List(new("other", "other-colony", f.Issue("other", "other-colony"))).Listings.Single().PriceSilver == 10,
            "Shop lost per-colony stepped pricing.");
        f.Recovery.OnReplay = () => f.World.RevokeAdministrator("userA");
        Check(!f.Shop.Remove(new("userA", "colonyA", "listing", f.Token)).Result.Accepted && f.ShopRegistry.Find("listing") is not null,
            "Administrator revoked during recovery removed a listing.");
        f.Recovery.OnReplay = null; f.World.PromoteAdministrator("userA");
        Check(f.Shop.Remove(new("userA", "colonyA", "listing", f.Token)).Removed && f.ShopRegistry.List().Count == 0, "Authorized removal failed.");
    }

    private static void MaximumQuantity(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new EconomicModuleTestContext(factory);
        Seed(f, ServerShopListingKinds.BuyFromPlayer, item: new("requirement", "Steel", int.MaxValue));
        var request = Command(f, ServerShopListingKinds.BuyFromPlayer, count: 1, delivered: new[] { new ThingReferenceDto("steel", "Steel", int.MaxValue) });
        Check(f.Shop.Purchase(request, f.Package.Payload, f.Token).Result.Accepted && f.ShopRegistry.Find("listing")!.StockCount == 4,
            "Overflow rejection also rejected the largest representable quantity.");
    }

    private static void CopyComplexItem()
    {
        var item = new ThingReferenceDto("complex", "MinifiedThing", 2, quality: "Excellent", hitPoints: 80,
            minifiedInnerDefName: "Bed", minifiedInnerStuffDefName: "WoodLog", minifiedInnerQuality: "Good", minifiedInnerHitPoints: 55,
            wornByCorpse: true, biocoded: true, biocodedPawnLabel: "pawn", biocodedPawnGlobalId: "global", displayLabel: "复杂 é", marketValue: 123,
            uniqueWeapon: true, uniqueWeaponTraits: new[] { "trait" }, stuffDefName: "Steel", maxHitPoints: 100, minifiedInnerMaxHitPoints: 75,
            metadata: new Dictionary<string, string?> { ["extension"] = "state" }, thingPackage: new(1, "package", "Bed", "bed", 2, new("state", "hash", 4)), thingPackageId: "id");
        var normalized = ShopRules.NormalizeItem(item); var expanded = ShopRules.MultiplyRequirement(item, 3);
        var expected = JsonSerializer.SerializeToNode(item)!.AsObject();
        var normalizedJson = JsonSerializer.SerializeToNode(normalized)!.AsObject(); expected[nameof(item.MarketValue)] = null;
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, normalizedJson) && !ReferenceEquals(item.Metadata, normalized.Metadata), "Normalization lost complex state or shared mutable metadata.");
        expected[nameof(item.MarketValue)] = 123; expected[nameof(item.StackCount)] = 6;
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(expanded)), "Quantity expansion lost complex fields.");
    }

    private static readonly ProtocolIdentity Buyer = new("userA", "colonyA", "before");
    private static void Seed(EconomicModuleTestContext f, string kind, int price = 10, int stock = 5, ThingReferenceDto? item = null) =>
        f.ShopRegistry.Upsert("listing", kind, item ?? new("requirement", "Steel", 2), price, stock, 0, "AtLeast", "AtLeast", "root", DateTimeOffset.UnixEpoch);
    private static PurchaseServerShopListingWithSnapshotRequest Command(EconomicModuleTestContext f, string kind, ProtocolIdentity? buyer = null,
        int price = 10, int count = 2, IReadOnlyList<ThingReferenceDto>? delivered = null) => new(
        new("purchase-é", buyer ?? Buyer, "listing", price, price * count, count, kind, delivered ?? new[] { new ThingReferenceDto("steel", "Steel", count * 2) }),
        ProtocolDtoMapper.ToMetadataDto(f.Package));
    private static string Evidence(EconomicModuleTestContext f) => JsonSerializer.Serialize(new { Listings = f.ShopRegistry.List(),
        Receipt = f.ShopRegistry.FindReceipt("purchase-é"), Count = f.ShopRegistry.GetBuyerPurchaseCount("listing", Buyer),
        Snapshot = f.Snapshots.GetLatest("userA", "colonyA"), Events = f.Ledger.ListAll() });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
