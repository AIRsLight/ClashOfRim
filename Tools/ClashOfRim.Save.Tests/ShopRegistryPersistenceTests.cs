using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class ShopRegistryPersistenceTests
{
    private static readonly ProtocolIdentity Buyer = new("buyer", "colony", null);

    public static void Run()
    {
        foreach (bool reopen in new[] { false, true })
        foreach (string operation in new[] { "upsert", "remove", "purchase" })
        foreach (string table in operation == "purchase"
                     ? new[] { "server_shop_listings", "server_shop_buyer_purchases", "server_shop_completed_purchases" }
                     : operation == "remove" ? new[] { "server_shop_listings", "server_shop_buyer_purchases" }
                     : new[] { "server_shop_listings" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-shop-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                ServerShopRegistry Open() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null);
                var shop = Open();
                Seed(shop, 5);
                Require(Purchase(shop, "first").Accepted, "Seed purchase failed.");
                var cached = shop.List();
                Execute(database, $"create trigger fail_shop before {(operation == "remove" ? "delete" : "insert")} on {table} begin select raise(abort, 'injected failure'); end;");
                bool failed = false;
                try { Mutate(shop, operation); } catch (SqliteException) { failed = true; }
                Require(failed, "Shop persistence failure was not injected.");
                Require(ReferenceEquals(cached, shop.List()), "Failed write invalidated the listing cache.");
                VerifyBefore(shop);
                VerifyBefore(Open());
                Execute(database, "drop trigger fail_shop;");
                if (reopen) shop = Open();
                Mutate(shop, operation);
                VerifyAfter(shop, operation);
                VerifyAfter(Open(), operation);
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }

        foreach (string operation in new[] { "upsert", "remove", "purchase" })
        {
            var persistence = new FailingSlot();
            var shop = new ServerShopRegistry(persistence);
            Seed(shop, 5);
            Purchase(shop, "first");
            persistence.Fail = true;
            bool failed = false;
            try { Mutate(shop, operation); } catch (IOException) { failed = true; }
            Require(failed, "Legacy JSON failure was not injected.");
            VerifyBefore(shop);
            VerifyBefore(new ServerShopRegistry(persistence));
            persistence.Fail = false;
            Mutate(shop, operation);
            VerifyAfter(new ServerShopRegistry(persistence), operation);
        }
    }

    private static void Seed(ServerShopRegistry shop, int stock) => shop.Upsert("listing", ServerShopListingKinds.SellToPlayer,
        new ThingReferenceDto("item", "Steel", 1), 10, stock, 1, ServerShopQualityRequirementModes.AtLeast,
        ServerShopHitPointsRequirementModes.AtLeast, "admin", DateTimeOffset.UnixEpoch);

    private static ServerShopPurchaseResult Purchase(ServerShopRegistry shop, string key) =>
        shop.TryPurchase(key, "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer, DateTimeOffset.UnixEpoch);

    private static void Mutate(ServerShopRegistry shop, string operation)
    {
        if (operation == "upsert") Seed(shop, 9);
        else if (operation == "remove") Require(shop.Remove("listing"), "Listing was lost before removal.");
        else Require(Purchase(shop, "second").Accepted, "Purchase retry was rejected.");
    }

    private static void VerifyBefore(ServerShopRegistry shop)
    {
        Require(shop.Find("listing")?.StockCount == 4 && shop.GetBuyerPurchaseCount("listing", Buyer) == 1,
            "Failed write changed shop stock or price history.");
        Require(!shop.ValidatePurchase("second", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer).Duplicate,
            "Failed purchase was marked complete.");
    }

    private static void VerifyAfter(ServerShopRegistry shop, string operation)
    {
        int? stock = operation == "remove" ? null : operation == "upsert" ? 9 : 3;
        int count = operation == "remove" ? 0 : operation == "upsert" ? 1 : 2;
        Require(shop.Find("listing")?.StockCount == stock && shop.GetBuyerPurchaseCount("listing", Buyer) == count,
            "Retry lost stock or price history.");
        if (operation == "purchase") Require(Purchase(shop, "second").Duplicate && shop.Find("listing")!.StockCount == 3,
            "Retry purchased twice.");
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FailingSlot : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public string? Read() => value;
        public void Write(string json)
        {
            if (Fail) throw new IOException("Injected JSON write failure.");
            value = json;
        }
    }
}
