using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    private JsonElement raidLoss;
    internal static string[] StabilityCases(string? kind) => kind switch
    {
        "account" => ["account-password-change", "account-achievements-before-abandon", "account-liabilities-before-abandon", "account-abandon-owner", "account-rebuild-owner", "account-achievements-new-generation", "account-abandon-guest", "account-rebuild-guest"],
        "bank" => ["bank-input-rejection", "bank-overdue-repayment", "bank-mercenary-debt"],
        "relocation" => ["relocation-caravan", "relocation-quest-site"],
        "gravship" => ["gravship-new-map", "gravship-quest-site"],
        "transfers" => ["alliance-request-accept", "remote-map-isolation-repeat", "pawn-state-fixture", "caravan-load-travel",
            "support-state-early-return", "support-state-rejected-return", "support-state-expired-return", "raid-loss-reload-settlement"],
        "catalog" => ["shop-complete-catalog"],
        "catalog-sample" => ["shop-selected-catalog"],
        "stress" => ["shop-large-data"],
        "items" => ["complex-item-fixture", "complex-item-cancel-refund", "complex-item-trade-delivery"],
        "permanent" => ["alliance-request-accept", "caravan-load-travel", "support-permanent-state"],
        "death" => ["alliance-request-accept", "caravan-load-travel", "support-death-expiry"],
        _ => []
    };
    async Task RunStability()
    {
        if (profile.Stability == "account") await AccountLifecycle();
        if (profile.Stability == "gravship")
        {
            await Case("gravship-new-map", () => GravshipMove("A", false));
            await Case("gravship-quest-site", () => GravshipMove("B", true));
        }
        if (profile.Stability == "relocation")
        {
            await Case("relocation-caravan", () => Relocate("A", "caravan"));
            await Case("relocation-quest-site", () => Relocate("B", "site"));
        }
        if (profile.Stability == "bank")
        {
            await Case("bank-input-rejection", BankBounds);
            await Case("bank-overdue-repayment", BankOverdue);
            await Case("bank-mercenary-debt", BankDebt);
        }
        if (profile.Stability == "catalog") await Case("shop-complete-catalog", ShopCatalog);
        if (profile.Stability == "catalog-sample") await Case("shop-selected-catalog", ShopCatalog);
        if (profile.Stability == "stress") await Case("shop-large-data", ShopStress);
        if (profile.Stability == "items") await ComplexItems();
        if (profile.Stability is "permanent" or "death")
        {
            await Case("alliance-request-accept", Alliance);
            await Case("caravan-load-travel", Travel);
            await Case(profile.Stability == "death" ? "support-death-expiry" : "support-permanent-state", () => Support(profile.Stability, true));
        }
        if (profile.Stability == "transfers")
        {
            await Case("alliance-request-accept", Alliance);
            await Case("remote-map-isolation-repeat", async () => { for (int i = 0; i < 3; i++) await ObserveAlly(); });
            await Case("pawn-state-fixture", async () =>
            {
                var home = (await Call("A", "clash.inventory")).GetProperty("maps")[0];
                await Call("A", "clash.fixture.pawnState", new { pawn = H(home.GetProperty("colonists")[0]), acknowledgeFixture = true });
            });
            await Case("caravan-load-travel", Travel);
            await Case("support-state-early-return", () => Support("early", true));
            await Case("support-state-rejected-return", () => Support("reject", true));
            await Case("support-state-expired-return", () => Support("expiry", true));
            await Case("raid-loss-reload-settlement", async () =>
            {
                int before = await Stock("B", "Steel");
                await Raid(); await LoginExisting(true);
                Check(await Stock("B", "Steel") == before - raidLoss.Int("count"), "Defender reload did not preserve battle loss exactly once.");
            });
        }
    }
    async Task AwaitSupportReturn(JsonElement original, bool verify)
    {
        var caravans = await Wait("A", "caravans.list", null, p => Items(p).Sum(c => c.GetProperty("pawns").GetArrayLength()) == 1);
        var caravan = Items(caravans).Single(c => c.GetProperty("pawns").GetArrayLength() == 1);
        expedition = H(caravan); await Idle("A");
        if (verify)
        {
            var returned = await Call("A", "clash.stability.pawn", new { pawn = caravan.GetProperty("pawns")[0].String("handle") });
            Equal(original.GetProperty("data"), returned.GetProperty("data"), "Support return changed stable pawn state");
        }
    }
    async Task ReloadActiveBattle(string eventId)
    {
        string epoch = (await Call("A", "game.state")).String("epoch");
        var before = await Call("A", "clash.remote");
        await Call("A", "game.save", new { name = "rimtest-active-battle" });
        await Call("A", "game.load", new { name = "rimtest-active-battle" });
        await Wait("A", "game.state", null, s => s.String("epoch") != epoch && s.Bool("playerHasControl"), 180);
        await Call("A", "time.set", new { speed = "Paused" });
        var after = await Wait("A", "clash.remote", null, s => s.Bool("active"));
        Check(after.GetProperty("session").String("relatedEventId") == eventId && after.GetProperty("session").Bool("isRaidBattle"), "Reload lost active battle identity.");
        foreach (string field in new[] { "targetSnapshotId", "targetMapId", "clientMapId" })
            Equal(before.GetProperty("session").GetProperty(field), after.GetProperty("session").GetProperty(field), "Reload changed battle " + field);
    }
}
