using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class ServiceSnapshotReplayRaceTests
{
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        SnapshotEndpointRecoveryPreparationTests.RunServices(fixture);
        foreach (string kind in new[] { "shop-sell", "shop-buy", "mercenary", "guard", "raid", "guarded-raid" })
        foreach (string mode in new[] { "same", "different", "revoke", "replace" }) RunScenario(fixture, kind, mode);
    }

    private static void RunScenario(Func<string, SaveSnapshotPackage> fixture, string kind, string mode)
    {
        var state = new ClashOfRimNetworkState(serverConfiguration: MercenaryCommitCrashTests.Configuration());
        bool raid = kind.EndsWith("raid"), guarded = kind == "guarded-raid", shop = kind.StartsWith("shop");
        string shopKind = kind == "shop-buy" ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
        var package = raid ? RaidCommitCrashTests.Setup(state, fixture, guarded)
            : shop ? fixture("paid") : SnapshotEndpointPreparation.Prepare(state.SnapshotStore, fixture, Actor, "paid");
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
            Receipts = Enumerable.Range(1, SnapshotReplayRace.ParticipantCount).Select(i => state.ServerShop.FindReceipt(Key(i))).ToArray(),
            Contracts = Enumerable.Range(1, SnapshotReplayRace.ParticipantCount).Select(i => state.MercenaryContracts.FindByIdempotencyKey(Key(i))).ToArray(),
            Guards = Enumerable.Range(1, SnapshotReplayRace.ParticipantCount).Select(i => state.MercenaryGuards.FindByIdempotencyKey(Key(i))).ToArray(),
            DefenderGuard = state.MercenaryGuards.FindByIdempotencyKey("guard")
        });
        string before = Evidence();
        var notifications = new[] { "userA", "userB" }.ToDictionary(u => u, state.EventNotifications.GetVersion);
        Reply[] replies = SnapshotReplayRace.Run(Call, () =>
        {
            if (mode == "revoke") state.LoginSessions.EndUser(Actor.UserId);
            if (mode == "replace") state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, DateTimeOffset.UtcNow, out _);
        });
        if (mode is "revoke" or "replace")
        {
            Check(replies.All(r => r.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable && r.SnapshotId is null && r.Lineage is null)
                && before == Evidence() && notifications.All(p => state.EventNotifications.GetVersion(p.Key) == p.Value),
                "Expired service session mutated or disclosed state: " + kind + "/" + mode);
            return;
        }
        var accepted = replies.Where(r => r.Result.Accepted).ToArray();
        Check(accepted.Length == (mode == "same" ? SnapshotReplayRace.ParticipantCount : 1) && accepted.Count(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1,
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

    private sealed record Reply(ProtocolResponse Result, string? Id, string? SnapshotId, string? Lineage, int? Stock = null);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
