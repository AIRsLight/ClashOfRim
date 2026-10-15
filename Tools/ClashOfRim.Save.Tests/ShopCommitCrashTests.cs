using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class ShopCommitCrashTests
{
    private static readonly ProtocolIdentity Buyer = new("userA", "colonyA", "before");

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool reopen in new[] { false, true })
        foreach (int fault in Enumerable.Range(0, 11))
            VerifyRecovery(fixture, fault, reopen);
        VerifyMigration();
        VerifyEndpoints(fixture).GetAwaiter().GetResult();
        VerifyEndpointRecovery(fixture).GetAwaiter().GetResult();
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, int fault, bool reopen)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-shop-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            string journal = Path.Combine(root, "intents");
            string snapshotDirectory = Path.Combine(root, "snapshots");
            ServerDatabaseMigrator.Migrate(database);
            ServerShopRegistry OpenShop() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null);
            var shop = OpenShop();
            Seed(shop);
            var files = new FileColonySnapshotIndexStore(snapshotDirectory);
            var bank = new BankLoanRegistry();
            if (fault == 1) PendingOperationCommitCrashTests.AddAncillary(bank);
            SaveSnapshotPackage package = fixture("shop-commit");
            LatestSnapshotRecord snapshot = new SnapshotUploadReceiver(files).Receive(
                new SnapshotUploadContext("userA", "colonyA", "shop-commit"), package,
                DateTimeOffset.UtcNow, storeAccepted: false).AcceptedSnapshot!;
            var mutation = shop.PrepareSnapshotCommit(Request(), snapshot);
            var commits = new SnapshotCommitService(journal);
            string[] tables = { "server_shop_listings", "server_shop_buyer_purchases", "server_shop_completed_purchases", "server_shop_purchase_receipts" };
            if (fault is >= 2 and <= 5) Execute(database,
                $"create trigger fail_shop before insert on {tables[fault - 2]} begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try
            {
                commits.CommitShop(new CrashStore(files, fault), bank, shop, package, snapshot, mutation,
                    (_, _) => { if (fault is 6 or 9) throw new IOException("post-upload"); });
            }
            catch (IOException) { failed = true; }
            catch (SqliteException) when (fault is >= 2 and <= 5) { failed = true; }
            Require(failed, "Shop fault was not injected.");
            if (fault is >= 2 and <= 5)
            {
                VerifyUncommitted(shop);
                VerifyUncommitted(OpenShop());
                Require(files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == "shop-commit", "Failure did not occur after snapshot publication.");
                Execute(database, "drop trigger fail_shop;");
            }
            if (fault is 7 or 9) Seed(shop, stock: 20); // Conflict before commit; preserve admin edit after a committed receipt.
            if (fault == 8)
            {
                var newer = fixture("newer");
                files.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (fault == 10) shop.TryPurchase("purchase", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1,
                new ProtocolIdentity("other", "other-colony", null), DateTimeOffset.UtcNow);
            if (reopen)
            {
                shop = OpenShop();
                files = new FileColonySnapshotIndexStore(snapshotDirectory);
                commits = new SnapshotCommitService(journal);
            }
            if (fault is 7 or 8 or 10)
            {
                bool rejected = false;
                try { commits.Recover(files, bank, (_, _) => { }, null, shop: shop); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && Directory.EnumerateFiles(journal, "*.json").Any()
                    && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == (fault == 8 ? "newer" : null)
                    && shop.FindReceipt("purchase") is null, "Conflicting state was overwritten or the journal was discarded.");
                return;
            }
            int processed = 0;
            commits.Recover(files, bank, (_, _) => processed++, null, shop: shop);
            commits.Recover(files, bank, (_, _) => processed++, null, shop: shop);
            Require(processed == 1 && !Directory.EnumerateFiles(journal, "*.json").Any(), "Recovery did not finish exactly once.");
            if (fault == 1) PendingOperationCommitCrashTests.VerifyAncillary(bank, "shop-commit");
            Require(files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken
                && files.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(package.Payload), "Recovery changed the accepted snapshot.");
            foreach (var recovered in new[] { shop, OpenShop() })
            {
                Require(recovered.FindReceipt("purchase") == mutation.Receipt && recovered.GetBuyerPurchaseCount("listing", Buyer) == 1
                    && recovered.Find("listing")!.StockCount == (fault == 9 ? 20 : 2), "Recovery lost or duplicated the purchase.");
                Require(recovered.ValidatePurchase("purchase", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer).Duplicate,
                    "Completed key was not saved with the receipt.");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void VerifyMigration()
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-shop-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var shop = new ServerShopRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null);
            Seed(shop);
            shop.TryPurchase("old-key", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer, DateTimeOffset.UtcNow);
            Execute(database, "drop table server_shop_purchase_receipts; update server_schema_metadata set metadata_value = '11' where metadata_key = 'schema_version';");
            var migration = ServerDatabaseMigrator.Migrate(database);
            Require(migration.AppliedMigrations.Contains("11->12"), "Shop migration step did not run.");
            shop = new ServerShopRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null);
            Require(shop.Find("listing")!.StockCount == 2 && shop.GetBuyerPurchaseCount("listing", Buyer) == 1
                && shop.FindReceipt("old-key") is null
                && shop.ValidatePurchase("old-key", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer).Duplicate,
                "Migration changed legacy stock, history or completed keys.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyEndpoints(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var package = fixture("shop-endpoint");
        foreach (string kind in new[] { ServerShopListingKinds.SellToPlayer, ServerShopListingKinds.BuyFromPlayer })
        {
            var state = new ClashOfRimNetworkState();
            Seed(state.ServerShop, kind: kind);
            var request = Request(kind: kind);
            var accepted = await Invoke(state, request, package, Buyer);
            Require(accepted.Result.Accepted && accepted.AppliedSnapshotId == "shop-endpoint"
                && !string.IsNullOrWhiteSpace(accepted.NextLineageToken), "Shop endpoint failed: " + JsonSerializer.Serialize(accepted));
            var replay = await Invoke(state, request, package, Buyer);
            Require(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && replay.AppliedSnapshotId == accepted.AppliedSnapshotId && replay.NextLineageToken == accepted.NextLineageToken
                && replay.RemainingStockCount == 2 && state.ServerShop.GetBuyerPurchaseCount("listing", Buyer) == 1,
                "Shop retry lost its snapshot identity or purchased twice.");
            var otherBuyer = new ProtocolIdentity("other", "other-colony", null);
            foreach (var altered in new[] { Request(kind: kind, buyer: otherBuyer), Request(kind: kind, listing: "other-listing"),
                         Request(kind: kind, count: 2), Request(kind: kind, unitPrice: 11), Request(kind: kind, totalPrice: 11),
                         Request(kind: kind == ServerShopListingKinds.SellToPlayer ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer) })
            {
                var rejected = await Invoke(state, altered, package, altered.Buyer);
                Require(rejected.Result.ErrorCode == ProtocolErrorCode.Conflict, "Replay accepted changed buyer or purchase parameters.");
            }
            var wrongSnapshot = await Invoke(state, request, fixture("other-snapshot"), Buyer);
            var forged = await Invoke(state, request, package, otherBuyer);
            Require(wrongSnapshot.Result.ErrorCode == ProtocolErrorCode.Conflict && forged.Result.ErrorCode == ProtocolErrorCode.Unauthorized,
                "Replay did not check snapshot identity or principal.");
            state.ServerShop.Remove("listing");
            replay = await Invoke(state, request, package, Buyer);
            Require(replay.Result.Accepted && replay.RemainingStockCount == 2, "Removing a listing destroyed its receipt.");
        }
        var legacyState = new ClashOfRimNetworkState();
        Seed(legacyState.ServerShop);
        legacyState.ServerShop.TryPurchase("purchase", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer, DateTimeOffset.UtcNow);
        Require((await Invoke(legacyState, Request(), package, Buyer)).Result.ErrorCode == ProtocolErrorCode.Conflict,
            "Unbound legacy key was accepted as a confirmed snapshot purchase.");

        var invalidState = new ClashOfRimNetworkState();
        Seed(invalidState.ServerShop);
        var invalid = await Invoke(invalidState, Request(), package with { Payload = new byte[] { 1 } }, Buyer);
        Require(!invalid.Result.Accepted && invalidState.SnapshotStore.ListLatest().Count == 0
            && invalidState.ServerShop.Find("listing")!.StockCount == 3, "Invalid snapshot published business state.");

        var raced = new ClashOfRimNetworkState();
        Seed(raced.ServerShop, stock: 1);
        var buyers = new[] { Buyer, new ProtocolIdentity("other", "other-colony", "before") };
        var attempts = buyers.Select((buyer, i) => Task.Run(async () =>
        {
            var candidate = package with { Envelope = package.Envelope with { Identity = new SnapshotIdentity(buyer.UserId, buyer.ColonyId!, "race-" + i) } };
            return await Invoke(raced, Request(key: "race-" + i, buyer: buyer), candidate, buyer);
        }));
        var results = await Task.WhenAll(attempts);
        Require(results.Count(r => r.Result.Accepted) == 1 && raced.SnapshotStore.ListLatest().Count == 1
            && raced.ServerShop.Find("listing")!.StockCount == 0
            && buyers.Sum(buyer => raced.ServerShop.GetBuyerPurchaseCount("listing", buyer)) == 1, "Concurrent buyers both purchased the last stock.");
    }

    private static async Task VerifyEndpointRecovery(Func<string, SaveSnapshotPackage> fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-shop-endpoint-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState Open() => new(
                snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
                serverShop: new ServerShopRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null),
                snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")));
            var state = Open();
            Seed(state.ServerShop);
            var package = fixture("endpoint-recovery");
            Execute(database, "create trigger fail_receipt before insert on server_shop_purchase_receipts begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { await Invoke(state, Request(), package, Buyer); }
            catch (SqliteException) { failed = true; }
            Require(failed, "Endpoint SQL failure was not injected.");
            string? acceptedToken = state.SnapshotStore.GetLatest("userA", "colonyA")?.Envelope.NextLineageToken;
            Require(!string.IsNullOrWhiteSpace(acceptedToken), "Endpoint did not publish a snapshot before SQL failure.");
            Execute(database, "drop trigger fail_receipt;");
            state = Open();
            var replay = await Invoke(state, Request(), package, Buyer);
            Require(replay.Result.Accepted && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && replay.AppliedSnapshotId == "endpoint-recovery" && replay.NextLineageToken == acceptedToken
                && state.ServerShop.Find("listing")!.StockCount == 2 && state.ServerShop.GetBuyerPurchaseCount("listing", Buyer) == 1
                && !Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any(),
                "Endpoint recovery failed to bind its durable purchase to the accepted snapshot.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static Task<PurchaseServerShopListingResponse> Invoke(ClashOfRimNetworkState state,
        PurchaseServerShopListingRequest request, SaveSnapshotPackage package, ProtocolIdentity principal) =>
        SnapshotEndpointTestClient.Invoke<PurchaseServerShopListingResponse>("PurchaseServerShopListingWithSnapshot", state,
            new PurchaseServerShopListingWithSnapshotRequest(request, ProtocolDtoMapper.ToMetadataDto(package)), package.Payload, principal);

    internal static PurchaseServerShopListingRequest Request(string key = "purchase", string kind = ServerShopListingKinds.SellToPlayer,
        ProtocolIdentity? buyer = null, string listing = "listing", int count = 1, int unitPrice = 10, int totalPrice = 10) =>
        new(key, buyer ?? Buyer, listing, unitPrice, totalPrice, count, kind, new[] { new ThingReferenceDto("steel", "Steel", count) });

    internal static void Seed(ServerShopRegistry shop, int stock = 3, string kind = ServerShopListingKinds.SellToPlayer) =>
        shop.Upsert("listing", kind, new ThingReferenceDto("steel", "Steel", 1), 10, stock, 1,
            ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "admin", DateTimeOffset.UnixEpoch);

    private static void VerifyUncommitted(ServerShopRegistry shop) => Require(shop.Find("listing")!.StockCount == 3
        && shop.GetBuyerPurchaseCount("listing", Buyer) == 0 && shop.FindReceipt("purchase") is null
        && !shop.ValidatePurchase("purchase", "listing", ServerShopListingKinds.SellToPlayer, 10, 10, 1, Buyer).Duplicate,
        "Failed SQL batch leaked stock, price history, receipt or completion flag.");

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class CrashStore(IColonySnapshotPackageStore inner, int fault) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (fault is 0 or 7 or 8 or 10) throw new IOException("before snapshot");
            inner.StoreLatest(package, index, at);
            if (fault == 1) throw new IOException("after snapshot");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
