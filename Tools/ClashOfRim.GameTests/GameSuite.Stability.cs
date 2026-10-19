using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    internal static string[] StabilityCases(string? kind) => kind switch
    {
        "offline-delivery" => ["alliance-request-accept", "caravan-load-travel", "gift-offline-deferred-accept", "gift-offline-reject-return", "notification-offline-policy"],
        "account" => ["account-password-change", "account-achievements-before-abandon", "account-liabilities-before-abandon", "account-abandon-owner", "account-rebuild-owner", "account-achievements-new-generation", "account-abandon-guest", "account-rebuild-guest"],
        "bank" => ["bank-input-rejection", "bank-overdue-repayment", "bank-mercenary-debt", "mercenary-guard-purchase"],
        "relocation" => ["relocation-caravan", "relocation-quest-site"],
        "gravship" => ["gravship-new-map", "gravship-quest-site"],
        "transfers" => ["alliance-request-accept", "remote-map-isolation-repeat", "pawn-state-fixture", "caravan-load-travel",
            "support-state-early-return", "support-state-rejected-return", "support-state-expired-return", "raid-loss-reload-settlement"],
        "support-returns" => ["alliance-request-accept", "pawn-state-fixture", "caravan-load-travel",
            "support-state-early-return", "support-state-rejected-return", "support-state-expired-return", "support-death-expiry"],
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
        if (profile.Stability == "offline-delivery")
        {
            await Case("alliance-request-accept", Alliance);
            await Case("caravan-load-travel", caravanFixture.Travel);
            await Case("gift-offline-deferred-accept", giftScenario.OfflineAccept);
            await Case("gift-offline-reject-return", giftScenario.OfflineReject);
            await Case("notification-offline-policy", notifications.OfflineDelivery);
        }
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
            await Case("bank-input-rejection", bank.InputBounds);
            await Case("bank-overdue-repayment", bank.OverdueRepayment);
            await Case("bank-mercenary-debt", bank.MercenaryDebt);
            await Case("mercenary-guard-purchase", mercenaries.GuardPurchase);
        }
        if (profile.Stability == "catalog") await Case("shop-complete-catalog", () => shopCatalog.Run(profile.CatalogDefinitions));
        if (profile.Stability == "catalog-sample") await Case("shop-selected-catalog", () => shopCatalog.Run(profile.CatalogDefinitions));
        if (profile.Stability == "stress") await Case("shop-large-data", shopStress.Run);
        if (profile.Stability == "items") await complexItems.Run();
        if (profile.Stability is "permanent" or "death")
        {
            await Case("alliance-request-accept", Alliance);
            await Case("caravan-load-travel", caravanFixture.Travel);
            await Case(profile.Stability == "death" ? "support-death-expiry" : "support-permanent-state", () => support.Run(profile.Stability, true));
        }
        if (profile.Stability is "transfers" or "support-returns")
        {
            string? fixturePawn = null;
            await Case("alliance-request-accept", Alliance);
            if (profile.Stability == "transfers")
                await Case("remote-map-isolation-repeat", async () => { for (int i = 0; i < 3; i++) await remoteMaps.ObserveAlly(true); });
            await Case("pawn-state-fixture", async () =>
            {
                var home = (await Call("A", "clash.inventory")).GetProperty("maps")[0];
                fixturePawn = H(home.GetProperty("colonists")[0]);
                await Call("A", "clash.fixture.pawnState", new { pawn = fixturePawn, acknowledgeFixture = true });
            });
            await Case("caravan-load-travel", () => caravanFixture.Travel(fixturePawn));
            await Case("support-state-early-return", () => support.Run("early", true));
            await Case("support-state-rejected-return", () => support.Run("reject", true));
            await Case("support-state-expired-return", () => support.Run("expiry", true));
            if (profile.Stability == "support-returns")
                await Case("support-death-expiry", () => support.Run("death", true));
            else await Case("raid-loss-reload-settlement", async () =>
            {
                int before = await colonyFixture.Stock("B", "Steel");
                var result = await raids.Run(verifyLoss: true); await entry.LoginExisting(true);
                Check(result.DefenderLossCount is int loss && await colonyFixture.Stock("B", "Steel") == before - loss, "Defender reload did not preserve battle loss exactly once.");
            });
        }
    }
}
