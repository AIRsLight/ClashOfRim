namespace ClashOfRim.GameTests;

// The sole source of profile selection, execution order and case reporting policy.
internal static class ScenarioCatalog
{
    internal static readonly ScenarioGroup Startup = ScenarioGroup.Cases("startup", "launch-profile", "shared-world-entry");
    internal static readonly ScenarioGroup World = new("world",
        new("capture-world", Report: false),
        new("world-parameters", ContinueOnFailure: true), new("world-geometry", ContinueOnFailure: true),
        new("world-tile-data", ContinueOnFailure: true), new("world-feature-identities", ContinueOnFailure: true),
        new("storyteller-difficulty", ContinueOnFailure: true), new("culture-references", ContinueOnFailure: true),
        new("world-persistence", ContinueOnFailure: true), new("storyteller-permission"), new("runtime-difficulty-sync"));
    internal static readonly ScenarioGroup Features = new("features",
        new("account-password-change"), new("colony-appearance-sync"), new("discover-players", Report: false),
        new("public-chat"), new("private-chat-read"), new("unicode-chat-blank-rejection"),
        new("admin-configuration-permissions"), new("admin-action-permissions"), new("admin-role-grant-revoke"),
        new("admin-broadcast"), new("admin-kick-reconnect"), new("admin-ban-unban"), new("maintenance-password-rejection"),
        new("bank-input-rejection"), new("bank-loan-repayment"), new("shop-purchase-stock"), new("shop-buyback-stock-rejection"),
        new("trade-cancel-refund"), new("trade-memo-cancel"), new("trade-drop-pod-fulfillment"), new("achievement-unlock-leaderboard"),
        new("alliance-request-accept"), new("ally-observation-close"), new("caravan-load-travel"), new("trade-caravan-fulfillment"),
        new("caravan-gift-accept"), new("support-send-early-return"), new("mercenary-hire"), new("mercenary-recall"), new("raid-open-settle"),
        new("refresh-features", Report: false));
    internal static readonly ScenarioGroup GiftReplay = ScenarioGroup.Cases("gift-replay",
        "alliance-request-accept", "caravan-load-travel", "caravan-gift-accept", "gift-repeat-accept");
    internal static readonly ScenarioGroup Offline = ScenarioGroup.Cases("offline-delivery",
        "alliance-request-accept", "caravan-load-travel", "gift-offline-deferred-accept", "gift-offline-reject-return", "notification-offline-policy");
    internal static readonly ScenarioGroup Account = ScenarioGroup.Cases("account", "account-password-change", "account-achievements-before-abandon",
        "account-liabilities-before-abandon", "account-abandon-owner", "account-rebuild-owner", "account-achievements-new-generation", "account-abandon-guest", "account-rebuild-guest");
    internal static readonly ScenarioGroup Bank = ScenarioGroup.Cases("bank", "bank-input-rejection", "bank-overdue-repayment", "bank-mercenary-debt", "mercenary-guard-purchase");
    internal static readonly ScenarioGroup Combat = ScenarioGroup.Cases("combat", "baseline-entry-rejection", "alliance-request-accept", "combat-fixture", "remote-pawn-factions-ai", "caravan-load-travel", "raid-defense-points-ai");
    internal static readonly ScenarioGroup ThirdPartyPawns = ScenarioGroup.Cases("third-party-pawns", "alien-statue-fixture", "statue-native-reload-control", "alien-statue-cancel", "alien-statue-delivery-reload", "alliance-request-accept", "caravan-load-travel", "vef-pawn-support-restore");
    internal static readonly ScenarioGroup ThirdPartyMaps = ScenarioGroup.Cases("third-party-maps", "alliance-request-accept", "third-party-map-fixture", "third-party-map-projection", "caravan-load-travel", "third-party-vehicle-defense");
    internal static readonly ScenarioGroup Relocation = ScenarioGroup.Cases("relocation", "relocation-caravan", "relocation-quest-site");
    internal static readonly ScenarioGroup Gravship = ScenarioGroup.Cases("gravship", "gravship-new-map", "gravship-quest-site");
    internal static readonly ScenarioGroup Transfers = ScenarioGroup.Cases("transfers", "alliance-request-accept", "remote-map-isolation-repeat", "pawn-state-fixture",
        "caravan-load-travel", "support-state-early-return", "support-state-rejected-return", "support-state-expired-return", "raid-loss-reload-settlement");
    internal static readonly ScenarioGroup SupportReturns = ScenarioGroup.Cases("support-returns", "alliance-request-accept", "pawn-state-fixture",
        "caravan-load-travel", "support-state-early-return", "support-state-rejected-return", "support-state-expired-return", "support-death-expiry");
    internal static readonly ScenarioGroup Catalog = ScenarioGroup.Cases("catalog", "shop-complete-catalog");
    internal static readonly ScenarioGroup CatalogSample = ScenarioGroup.Cases("catalog-sample", "shop-selected-catalog");
    internal static readonly ScenarioGroup Stress = ScenarioGroup.Cases("stress", "shop-large-data");
    internal static readonly ScenarioGroup Items = ScenarioGroup.Cases("items", "complex-item-fixture", "complex-item-cancel-refund", "complex-item-trade-delivery");
    internal static readonly ScenarioGroup Permanent = ScenarioGroup.Cases("permanent", "alliance-request-accept", "caravan-load-travel", "support-permanent-state");
    internal static readonly ScenarioGroup Death = ScenarioGroup.Cases("death", "alliance-request-accept", "caravan-load-travel", "support-death-expiry");
    // Reload last so a broken serialized world cannot hide independent live feature results.
    internal static readonly ScenarioGroup Finish = ScenarioGroup.Cases("finish", "snapshot-save-reload", "session-reconnect", "runtime-errors");
    private static readonly IReadOnlyDictionary<string, ScenarioGroup> stability = new[]
    { Offline, Account, Bank, Combat, ThirdPartyPawns, ThirdPartyMaps, Relocation, Gravship, Transfers, SupportReturns, Catalog, CatalogSample, Stress, Items, Permanent, Death }
        .ToDictionary(g => g.Name, StringComparer.Ordinal);
    internal static bool SupportsStability(string kind) => stability.ContainsKey(kind);
    internal static ScenarioPlan Plan(SuiteProfile profile)
    {
        profile.Validate();
        ScenarioGroup? selected = profile.Features ? Features : profile.GiftReplay ? GiftReplay
            : profile.Stability is { } name ? stability[name] : null;
        return selected is null ? new(Startup, World, Finish) : new(Startup, World, selected, Finish);
    }
}
