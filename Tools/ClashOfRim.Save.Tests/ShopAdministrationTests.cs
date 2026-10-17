using static RecoveryTestSupport;
using System.Net;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class ShopAdministrationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action action) { action(); count++; }
        foreach (bool remove in new[] { false, true })
        {
            foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-user", "missing-colony", "empty-world", "nonadmin", "logout", "replace", "expired", "malformed", "empty", "null" })
                Case(() => Invalid(factory, remove, invalid));
            foreach (string field in remove ? new[] { "listing" } : new[] { "item", "kind", "price", "stock", "pawn" })
                Case(() => Invalid(factory, remove, field));
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "role", "expired", "failure", "revoke-failure", "compete" })
                Case(() => Preparing(factory, remove, change));
            foreach (string change in new[] { "revoke", "replace", "role" }) Case(() => Published(factory, remove, change));
        }
        Case(() => CurrentBaseline(factory));
        foreach (bool recreate in new[] { false, true }) Case(() => CurrentListing(factory, recreate));
        Case(() => Concurrent(factory));
        foreach (bool remove in new[] { false, true })
        foreach (bool fail in new[] { false, true }) Case(() => Persistence(remove, fail));
        Console.WriteLine($"Shop administrator scenarios passed: {count}");
    }
    private static void Invalid(Func<string, SaveSnapshotPackage> factory, bool remove, string invalid)
    {
        using var e = new Env(factory, invalid); Change(e, invalid);
        string before = e.F.Evidence(), shop = e.Shop(); var admins = e.F.State.WorldConfiguration.ListAdministrators().ToArray();
        using var response = e.Call(remove, invalid);
        if (response.StatusCode != HttpStatusCode.BadRequest) Denied(response, remove);
        else Require(invalid is "malformed" or "empty" or "null", "Unexpected binding failure: " + invalid);
        e.F.AssertPending(before, "shop/" + invalid); Require(shop == e.Shop(), "Denied request changed shop.");
        Require(admins.SequenceEqual(e.F.State.WorldConfiguration.ListAdministrators()), "Shop request assigned an administrator.");
    }
    private static void Preparing(Func<string, SaveSnapshotPackage> factory, bool remove, string change)
    {
        using var e = new Env(factory); string? before = null; string shop = e.Shop(); HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(remove), () =>
            {
                Change(e, change);
                if (change is "failure" or "revoke-failure") e.F.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(e.F.State);
                before = e.F.Evidence();
            });
        using (response)
        {
            if (change is "none" or "compete") { Require(Accepted(response!), "Authorized recovery failed."); e.Changed(remove); e.F.AssertRecovered(); }
            else
            {
                if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure was swallowed.");
                else Denied(response!, remove);
                Require(shop == e.Shop(), "Obsolete administrator changed shop."); e.F.AssertPending(before!, "shop/preparation/" + change);
                e.F.Store.FailPreparation = false; e.F.RenewToken(); e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
                using var retry = e.Call(remove); Require(Accepted(retry), "Fresh administrator could not retry."); e.Changed(remove); e.F.AssertRecovered();
            }
        }
    }
    private static void Published(Func<string, SaveSnapshotPackage> factory, bool remove, string change)
    {
        using var e = new Env(factory); string shop = e.Shop(); e.Publication = () => Change(e, change);
        using var response = e.Call(remove); Denied(response, remove); Require(e.Shop() == shop, "Publication bypassed shop reauthorization."); e.F.AssertRecovered();
        e.Publication = null; e.F.RenewToken(); e.F.State.WorldConfiguration.PromoteAdministrator("receiver");
        using var retry = e.Call(remove); Require(Accepted(retry), "Retry after publication failed."); e.Changed(remove);
    }
    private static void CurrentBaseline(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); string shop = e.Shop(); HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(false), () => e.F.State.AdminBaseline.Submit(new SubmitAdminBaselineRequest(ProtocolApiVersion.Current, "receiver", "away", DateTimeOffset.UtcNow, [new("WoodLog", 1)], [])));
        using (response) Denied(response!, false);
        e.F.AssertRecovered(); Require(e.Shop() == shop, "Shop accepted a definition removed while recovery prepared.");
    }
    private static void CurrentListing(Func<string, SaveSnapshotPackage> factory, bool recreate)
    {
        using var e = new Env(factory); HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => response = e.Call(true), () => { e.F.State.ServerShop.Remove("listing"); if (recreate) e.SeedListing(9); });
        using (response) Require(Accepted(response!) == recreate, "Removal ignored current listing state.");
        Require(e.F.State.ServerShop.Find("listing") is null, "Removal left a stale listing."); e.F.AssertRecovered();
    }
    private static void Concurrent(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory);
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => { using var r = e.Call(false); Require(Accepted(r), "Concurrent update failed."); })).ToArray());
        e.Changed(false); e.F.AssertRecovered();
        var removes = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { using var r = e.Call(true); return Accepted(r); })).ToArray();
        Task.WaitAll(removes); Require(removes.Count(r => r.Result) == 1 && e.F.State.ServerShop.List().Count == 0, "Concurrent removal succeeded more than once.");
    }
    private static void Persistence(bool remove, bool fail)
    {
        var slot = new GateSlot(); var shop = new ServerShopRegistry(slot);
        shop.Upsert("listing", "SellToPlayer", new("steel", "Steel", 1), 10, 5, 1, "AtLeast", "AtLeast", "original", DateTimeOffset.UtcNow);
        var state = new ClashOfRimNetworkState(serverShop: shop); slot.Gate = state.RaidSettlementSnapshotMutationGate;
        state.WorldConfiguration.Prepare("admin"); var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate("admin", "colony", now, out var session);
        string token = state.AuthTokens.Issue("admin", "admin", "colony", session, now);
        object request = remove ? new RemoveServerShopListingRequest("admin", "colony", "listing", token)
            : new UpsertServerShopListingRequest("admin", "colony", "listing", "SellToPlayer", new("steel", "Steel", 1), 12, 2, authToken: token);
        void Mutate()
        {
            if (remove) state.Shop.Remove((RemoveServerShopListingRequest)request);
            else state.Shop.Upsert((UpsertServerShopListingRequest)request);
        }
        string before = JsonSerializer.Serialize(shop.List()); slot.Fail = fail; bool failed = false;
        try { Mutate(); }
        catch (IOException) { failed = true; }
        Require(failed == fail, "Unexpected shop persistence outcome.");
        if (fail)
        {
            Require(JsonSerializer.Serialize(shop.List()) == before && JsonSerializer.Serialize(new ServerShopRegistry(slot).List()) == before, "Failed shop write changed cache or persistence.");
            slot.Fail = false; Mutate();
        }
        Require(remove ? shop.Find("listing") is null : shop.Find("listing") is { StockCount: 2, UpdatedByUserId: "admin" }, "Persisted shop mutation was lost.");
        Require(JsonSerializer.Serialize(shop.List()) == JsonSerializer.Serialize(new ServerShopRegistry(slot).List()), "Shop persisted and cached results differ.");
    }
    private sealed class GateSlot : IJsonPersistenceSlot
    {
        public object? Gate; public bool Fail; private string? value;
        public string? Read() => value;
        public void Write(string json)
        {
            Require(Gate is null || Monitor.IsEntered(Gate), "Shop persistence escaped the shared mutation gate.");
            if (Fail) throw new IOException("Injected shop persistence failure.");
            value = json;
        }
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
        if (change == "role") { f.State.WorldConfiguration.PromoteAdministrator("other-admin"); Require(f.State.WorldConfiguration.RevokeAdministrator("receiver"), "Role revocation failed."); }
    }
    private static bool Accepted(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); using var j = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()); return j.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean(); }
    private static void Denied(HttpResponseMessage response, bool remove)
    {
        Require(!Accepted(response), "Unauthenticated or obsolete shop administrator accepted.");
        using var j = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        if (remove) Require(!j.RootElement.GetProperty("removed").GetBoolean(), "Denied removal claimed success.");
        else Require(!j.RootElement.TryGetProperty("listing", out var listing) || listing.ValueKind == JsonValueKind.Null, "Denied update disclosed a listing.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public Action? Publication;
        public Env(Func<string, SaveSnapshotPackage> factory, string invalid = "")
        {
            var plugins = PublicationPlugins("test.shop-admin", () => Publication?.Invoke());
            F = new(factory, plugins); F.Start();
            if (invalid != "empty-world") F.State.WorldConfiguration.Prepare(invalid == "nonadmin" ? "other-admin" : "receiver");
            SeedListing(5); F.Seed();
        }
        public void SeedListing(int stock) => F.State.ServerShop.Upsert("listing", "SellToPlayer", new("steel", "Steel", 1), 10, stock, 1, "AtLeast", "AtLeast", "original-admin", DateTimeOffset.UtcNow);
        public string Shop() => JsonSerializer.Serialize(F.State.ServerShop.List());
        public void Changed(bool remove)
        {
            var listing = F.State.ServerShop.Find("listing");
            Require(remove ? listing is null : listing is { StockCount: 2, PriceSilver: 12, UpdatedByUserId: "receiver" } && F.State.ServerShop.List().Count == 1, "Shop write lost fields or actor identity.");
        }
        public HttpResponseMessage Call(bool remove, string invalid = "")
        {
            var kind = remove ? ProtocolMessageKind.RemoveServerShopListing : ProtocolMessageKind.UpsertServerShopListing;
            if (invalid is "malformed" or "empty" or "null") return PostInvalidBody(F.Client, kind, invalid);
            return F.Post(kind, new { userId = invalid == "user" ? "intruder" : invalid == "missing-user" ? "" : "receiver",
                colonyId = invalid == "colony" ? "intruder" : invalid == "missing-colony" ? "" : "away",
                authToken = invalid == "missing-token" ? null : invalid == "token" ? "invalid" : F.Token,
                listingId = invalid == "listing" ? "" : "listing", listingKind = invalid == "kind" ? "invalid" : "SellToPlayer",
                item = invalid == "item" ? null : new ThingReferenceDto("steel", "Steel", 1, pawnPackageId: invalid == "pawn" ? "pawn" : null),
                priceSilver = invalid == "price" ? 0 : 12, stockCount = invalid == "stock" ? -1 : 2, priceIncreaseRatio = 1.2 });
        }
        public void Dispose() => F.Dispose();
    }
}
