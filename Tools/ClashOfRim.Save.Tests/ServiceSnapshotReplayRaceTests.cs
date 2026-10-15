using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class ServiceSnapshotReplayRaceTests
{
    private const int Count = 12;
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in new[] { "shop-sell", "shop-buy", "mercenary", "guard", "raid", "guarded-raid" })
        foreach (string mode in new[] { "same", "different", "revoke", "replace" }) RunScenario(fixture, kind, mode);
    }

    private static void RunScenario(Func<string, SaveSnapshotPackage> fixture, string kind, string mode)
    {
        var state = new ClashOfRimNetworkState(serverConfiguration: MercenaryCommitCrashTests.Configuration());
        bool raid = kind.EndsWith("raid"), guarded = kind == "guarded-raid", shop = kind.StartsWith("shop");
        string shopKind = kind == "shop-buy" ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
        var package = raid ? RaidCommitCrashTests.Setup(state, fixture, guarded)
            : shop ? fixture("paid") : MercenaryCommitCrashTests.PrepareEndpoint(state, fixture, Actor, "paid");
        if (shop) ShopCommitCrashTests.Seed(state.ServerShop, stock: 3, kind: shopKind);
        var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, now, out string session);
        string token = state.AuthTokens.Issue(Actor.UserId, Actor.UserId, Actor.ColonyId!, session, now);
        string Key(int i) => mode == "different" ? "race-" + i : "race";
        async Task<Reply> Call(int i)
        {
            if (raid)
            {
                var r = await RaidCommitCrashTests.Invoke(state, package, guarded, key: Key(i), token: token);
                return new(r.Result, r.EventId, r.AppliedSnapshotId, r.NextLineageToken);
            }
            if (shop)
            {
                var request = ShopCommitCrashTests.Request(Key(i), shopKind, Actor);
                var r = await SnapshotEndpointTestClient.Invoke<PurchaseServerShopListingResponse>("PurchaseServerShopListingWithSnapshot",
                    state, _ => new PurchaseServerShopListingWithSnapshotRequest(request, ProtocolDtoMapper.ToMetadataDto(package)), package.Payload, Actor, token);
                return new(r.Result, r.ListingId, r.AppliedSnapshotId, r.NextLineageToken, r.RemainingStockCount);
            }
            var hire = await MercenaryCommitCrashTests.Invoke(state, package, Actor, kind == "guard", key: Key(i),
                contractId: "contract-" + Key(i), token: token);
            return new(hire.Result, hire.ContractId, hire.SnapshotId, hire.Token);
        }
        string Evidence() => JsonSerializer.Serialize(new
        {
            Snapshots = state.SnapshotStore.ListLatest(), Events = state.Ledger.ListAll(),
            Listing = state.ServerShop.Find("listing"), BuyerCount = state.ServerShop.GetBuyerPurchaseCount("listing", Actor),
            Receipts = Enumerable.Range(1, Count).Select(i => state.ServerShop.FindReceipt(Key(i))).ToArray(),
            Contracts = Enumerable.Range(1, Count).Select(i => state.MercenaryContracts.FindByIdempotencyKey(Key(i))).ToArray(),
            Guards = Enumerable.Range(1, Count).Select(i => state.MercenaryGuards.FindByIdempotencyKey(Key(i))).ToArray(),
            DefenderGuard = state.MercenaryGuards.FindByIdempotencyKey("guard")
        });
        string before = Evidence();
        var notifications = new[] { "userA", "userB" }.ToDictionary(u => u, state.EventNotifications.GetVersion);
        Reply[] replies = Race(Call, () =>
        {
            if (mode == "revoke") state.LoginSessions.EndUser(Actor.UserId);
            if (mode == "replace") state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, DateTimeOffset.UtcNow, out _);
        });
        if (mode is "revoke" or "replace")
        {
            Check(replies.All(r => r.Result.ErrorCode == ProtocolErrorCode.Unauthorized && r.SnapshotId is null && r.Lineage is null)
                && before == Evidence() && notifications.All(p => state.EventNotifications.GetVersion(p.Key) == p.Value),
                "Expired service session mutated or disclosed state: " + kind + "/" + mode);
            return;
        }
        var accepted = replies.Where(r => r.Result.Accepted).ToArray();
        Check(accepted.Length == (mode == "same" ? Count : 1) && accepted.Count(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1,
            "Concurrent service replay rejected or duplicated: " + kind + "/" + mode + " " + JsonSerializer.Serialize(replies));
        var winner = accepted.Single(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest);
        Check(accepted.All(r => r.Id == winner.Id && r.SnapshotId == winner.SnapshotId && r.Lineage == winner.Lineage && r.Stock == winner.Stock),
            "Concurrent service replies disagree: " + kind);
        if (shop) Check(state.ServerShop.Find("listing")!.StockCount == 2 && state.ServerShop.GetBuyerPurchaseCount("listing", Actor) == 1, "shop double purchase");
        if (raid) Check(state.Ledger.ListByType(ServerEventType.Raid).Count == 1
            && (!guarded || state.MercenaryGuards.FindByIdempotencyKey("guard")!.ConsumedRaidEventId == winner.Id), "raid/guard double consumption");
        if (kind == "mercenary") Check(state.MercenaryContracts.CountActiveForColony(Actor.UserId, Actor.ColonyId!) == 1, "duplicate mercenary");
        if (kind == "guard") Check(state.MercenaryGuards.FindActiveForColony(Actor.UserId, Actor.ColonyId!)?.ContractId == winner.Id, "duplicate guard");
        Check(state.SnapshotStore.GetLatest(Actor.UserId, Actor.ColonyId!)?.Identity.SnapshotId == winner.SnapshotId, "confirmed snapshot absent");
        before = Evidence(); int winningAttempt = mode == "same" ? 1 : Array.FindIndex(replies, r => r.Result.Accepted) + 1;
        var replay = Call(winningAttempt).GetAwaiter().GetResult();
        Check(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Id == winner.Id && before == Evidence(), "later service replay changed state");
    }

    internal static T[] Race<T>(Func<int, Task<T>> call, Action beforeRelease)
    {
        var slots = (SemaphoreSlim)typeof(ClashOfRimNetworkServer).Assembly.GetType("AIRsLight.ClashOfRim.Network.SnapshotPreparationService")!
            .GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // .NET 8's synchronous semaphore waiter count confirms every endpoint reached preparation.
        // Keep this runtime-specific probe in tests; production has no scheduling/failure injection hook.
        var waitCount = typeof(SemaphoreSlim).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(int) && f.Name.Contains("waitCount", StringComparison.OrdinalIgnoreCase));
        int held = 0; while (slots.Wait(0)) held++;
        Check(held > 0, "preparation slots unexpectedly busy");
        var results = new T[Count]; var errors = new Exception?[Count]; var complete = new bool[Count];
        var threads = Enumerable.Range(0, Count).Select(i => new Thread(() =>
        {
            try { results[i] = call(i + 1).GetAwaiter().GetResult(); }
            catch (Exception ex) { errors[i] = ex; }
            finally { Volatile.Write(ref complete[i], true); }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var thread in threads) thread.Start();
            Check(SpinWait.SpinUntil(() => (int)waitCount.GetValue(slots)! == Count || complete.Any(c => c), TimeSpan.FromSeconds(15))
                && (int)waitCount.GetValue(slots)! == Count, "not every service request reached preparation: " + JsonSerializer.Serialize(results) + string.Join(";", errors.Select(e => e?.Message)));
            beforeRelease(); slots.Release(held); held = 0;
            foreach (var thread in threads) Check(thread.Join(TimeSpan.FromSeconds(30)), "service request timed out");
            if (errors.FirstOrDefault(e => e != null) is { } error) throw new Exception("service request failed", error);
            return results;
        }
        finally
        {
            if (held > 0) slots.Release(held);
            foreach (var thread in threads) if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    private sealed record Reply(ProtocolResponse Result, string? Id, string? SnapshotId, string? Lineage, int? Stock = null);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
