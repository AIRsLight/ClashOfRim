using static RecoveryTestSupport;
using System.Net;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class ShopListRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action action) { action(); count++; }
        foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-user", "missing-colony", "logout", "replace", "expired", "malformed", "empty", "null" })
            Case(() => Invalid(factory, invalid));
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "expired", "failure", "revoke-failure", "compete" })
            Case(() => Preparing(factory, change));
        foreach (string change in new[] { "logout", "replace", "revoke", "expired" }) Case(() => Published(factory, change));
        foreach (string change in new[] { "price", "stock", "purchase", "other-purchase", "remove", "recreate" })
        foreach (bool publication in new[] { false, true }) Case(() => CurrentListing(factory, change, publication));
        foreach (string kind in new[] { "SellToPlayer", "BuyFromPlayer" })
        foreach (string viewer in new[] { "receiver", "other-colony", "other-user" }) Case(() => Personalization(factory, kind, viewer));
        Case(() => Concurrent(factory));
        Console.WriteLine($"Shop list recovery scenarios passed: {count}");
    }
    private static void Invalid(Func<string, SaveSnapshotPackage> factory, string invalid)
    {
        using var e = new Env(factory); Change(e, invalid);
        string before = e.F.Evidence(), shop = e.Shop();
        using var response = e.Call(invalid);
        if (invalid is "malformed" or "empty" or "null") Require(response.StatusCode == HttpStatusCode.BadRequest, "Malformed shop query was accepted.");
        else Denied(response);
        e.F.AssertPending(before, "shop-list/" + invalid); Require(e.Shop() == shop, "Denied query changed shop state.");
    }
    private static void Preparing(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); string? before = null; string shop = e.Shop(); HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(), () =>
            {
                Change(e, change);
                if (change is "failure" or "revoke-failure") e.F.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(e.F.State);
                before = e.F.Evidence();
            });
        using (response)
        {
            if (change is "none" or "compete") { Check(response!, 10, 14, 1); e.F.AssertRecovered(); }
            else
            {
                if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure was swallowed.");
                else Denied(response!);
                e.F.AssertPending(before!, "shop-list/preparation/" + change);
                e.F.Store.FailPreparation = false; e.F.RenewToken();
                using var retry = e.Call(); Check(retry, 10, 14, 1); e.F.AssertRecovered();
            }
            Require(e.Shop() == shop, "Query changed stock or purchase counts.");
        }
    }
    private static void Published(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); string shop = e.Shop(); e.Publication = () => Change(e, change);
        using var response = e.Call(); Denied(response); e.F.AssertRecovered();
        Require(e.Shop() == shop, "Obsolete query changed shop state.");
        e.Publication = null; e.F.RenewToken(); using var retry = e.Call(); Check(retry, 10, 14, 1);
    }
    private static void CurrentListing(Func<string, SaveSnapshotPackage> factory, string change, bool publication)
    {
        using var e = new Env(factory); HttpResponseMessage? response = null;
        void Update()
        {
            if (change is "remove" or "recreate") e.F.State.ServerShop.Remove("listing");
            if (change is "price" or "stock" or "recreate") e.SeedListing(change == "price" ? 15 : 10, change == "price" ? 14 : 9);
            if (change == "purchase") e.Buy("receiver", "away", 1);
            if (change == "other-purchase") e.Buy("another", "away", 1);
        }
        if (publication) { e.Publication = Update; response = e.Call(); }
        else SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook, () => response = e.Call(), Update);
        using (response)
        {
            if (change == "remove") Require(Body(response!).Result.Accepted && Body(response!).Listings.Count == 0, "Query returned a deleted listing.");
            else Check(response!, change == "price" ? 15 : 10, change is "stock" or "recreate" ? 9 : change is "purchase" or "other-purchase" ? 13 : 14,
                change == "recreate" ? 0 : change == "purchase" ? 2 : 1);
        }
        e.F.AssertRecovered();
    }
    private static void Personalization(Func<string, SaveSnapshotPackage> factory, string kind, string viewer)
    {
        using var e = new Env(factory, kind);
        string user = viewer == "other-user" ? "another" : "receiver", colony = viewer == "other-colony" ? "other" : "away";
        e.F.State.LoginSessions.EndUser(user); var now = DateTimeOffset.UtcNow;
        e.F.State.LoginSessions.TryCreate(user, colony, now, out var session);
        string token = e.F.State.AuthTokens.Issue(user, user, colony, session, now), shop = e.Shop();
        using var response = e.F.Post(ProtocolMessageKind.ListServerShop, new { userId = user, colonyId = colony, authToken = token });
        Check(response, 10, 14, viewer == "other-user" ? 3 : viewer == "other-colony" ? 2 : 1);
        Require(Body(response).Listings.Single().ListingKind == kind && e.Shop() == shop, "Query changed listing direction or purchase history.");
        e.F.AssertRecovered();
    }
    private static void Concurrent(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); string shop = e.Shop();
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => { using var response = e.Call(); Check(response, 10, 14, 1); })).ToArray());
        Require(e.Shop() == shop, "Concurrent queries changed shop state."); e.F.AssertRecovered();
    }
    private static void Change(Env e, string change)
    {
        var f = e.F;
        if (change is "logout" or "replace" or "expired") f.State.LoginSessions.EndUser("receiver");
        if (change is "replace" or "expired")
        {
            f.State.LoginSessions.TryCreate("receiver", "away", change == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow, out var session);
            if (change == "expired") f.Token = f.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
        }
        if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
    }
    private static ListServerShopResponse Body(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<ListServerShopResponse>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
    private static void Denied(HttpResponseMessage response)
    { var body = Body(response); Require(!body.Result.Accepted && body.Listings.Count == 0, "Unauthorized shop query accepted or disclosed personalized listings."); }
    private static void Check(HttpResponseMessage response, int price, int stock, int count)
    {
        var body = Body(response); Require(body.Result.Accepted && body.Listings.Count == 1, "Authorized query failed.");
        var listing = body.Listings.Single();
        Require(listing.ListingId == "listing" && listing.BasePriceSilver == price && listing.PriceSilver == price * (1 << count)
            && listing.StockCount == stock && listing.BuyerPurchaseCount == count && listing.PriceIncreaseRatio == 2,
            "Query returned stale or another colony's price, stock, or purchase history.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public Action? Publication; private readonly string kind;
        public Env(Func<string, SaveSnapshotPackage> factory, string kind = "SellToPlayer")
        {
            this.kind = kind;
            var plugins = PublicationPlugins("test.shop-list", () => Publication?.Invoke());
            F = new(factory, plugins); F.Start(); SeedListing(10, 20);
            Buy("receiver", "away", 1); Buy("receiver", "other", 2); Buy("another", "away", 3); F.Seed();
        }
        public void SeedListing(int price, int stock) => F.State.ServerShop.Upsert("listing", kind, new("steel", "Steel", 1), price, stock, 2, "AtLeast", "AtLeast", "admin", DateTimeOffset.UtcNow);
        public void Buy(string user, string colony, int count)
        {
            var shop = F.State.ServerShop; var listing = shop.Find("listing")!; var buyer = new ProtocolIdentity(user, colony, null);
            int previous = shop.GetBuyerPurchaseCount("listing", buyer);
            Require(shop.TryPurchase(Guid.NewGuid().ToString(), "listing", kind, ServerShopRegistry.CalculateCurrentUnitPrice(listing, previous),
                ServerShopRegistry.CalculatePurchaseTotalPrice(listing, previous, count), count, buyer, DateTimeOffset.UtcNow).Accepted, "Fixture purchase failed.");
        }
        public string Shop() => JsonSerializer.Serialize(new { listings = F.State.ServerShop.List(), counts = new[] {
            F.State.ServerShop.GetBuyerPurchaseCount("listing", new("receiver", "away", null)),
            F.State.ServerShop.GetBuyerPurchaseCount("listing", new("receiver", "other", null)),
            F.State.ServerShop.GetBuyerPurchaseCount("listing", new("another", "away", null)) } });
        public HttpResponseMessage Call(string invalid = "")
        {
            if (invalid is "malformed" or "empty" or "null") return PostInvalidBody(F.Client, ProtocolMessageKind.ListServerShop, invalid);
            return F.Post(ProtocolMessageKind.ListServerShop, new { userId = invalid == "user" ? "another" : invalid == "missing-user" ? "" : "receiver",
                colonyId = invalid == "colony" ? "other" : invalid == "missing-colony" ? "" : "away",
                authToken = invalid == "missing-token" ? null : invalid == "token" ? "invalid" : F.Token });
        }
        public void Dispose() => F.Dispose();
    }
}
