using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;


namespace ClashOfRim.GameTests;

internal sealed class GameSuite
{
    private readonly string root, pipeA, pipeB;
    private readonly SuiteProfile profile;
    private readonly TestRun run;
    private readonly ScenarioSequence[] execution;
    private readonly Microsoft.AspNetCore.Builder.WebApplication server;
    private readonly Dictionary<string, RimTestClient> clients = new();
    private string url = "";
    public GameSuite(string root, string pipeA, string pipeB, SuiteProfile profile)
    {
        this.root = root; this.pipeA = pipeA; this.pipeB = pipeB; this.profile = profile;
        Directory.CreateDirectory(root);
        var plan = ScenarioCatalog.Plan(profile);
        run = new TestRun(root, plan.CaseIds);
        var dialogs = new ScenarioDialogs(run, profile.A.Language != profile.B.Language);
        var polling = new ScenarioPolling(run, dialogs);
        server = ClashOfRimNetworkServer.Build([
            "--contentRoot", Path.Combine(root, "Server"), "--urls", "http://127.0.0.1:0",
        "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Authentication:DebugMode", "false",
        "--Localization:Language", "English", "--Logging:LogLevel:Default", "Warning"]);
        var colonyFixture = new ColonyScenarioFixture(run, polling);
        var diagnostics = new ScenarioRuntimeDiagnostics(run);
        var serverState = server.Services.GetRequiredService<ClashOfRimNetworkState>();
        var economy = new EconomicScenarioFixture(run, polling, colonyFixture);
        var mercenaries = new MercenaryScenario(run, polling, colonyFixture, economy, serverState.MercenaryContracts, serverState.MercenaryGuards, () =>
        {
            var configuration = serverState.ServerConfiguration;
            return (configuration.MercenaryGuardApprenticeSilver, configuration.MercenaryGuardApprenticePointsRatio);
        });
        var caravanFixture = new CaravanScenario(run, polling, dialogs, colonyFixture);
        var tradeActions = new TradeScenarioFixture(run, polling, dialogs);
        var trades = new TradeScenario(run, polling, dialogs, colonyFixture, economy, caravanFixture, tradeActions);
        var achievements = new AchievementScenario(run, polling, trades, serverState.Achievements, serverState.SnapshotStore, serverState.Players);
        var complexItems = new ComplexItemScenario(run, polling, colonyFixture, tradeActions);
        var eventFixture = new EventScenarioFixture(polling, serverState.Ledger);
        var support = new SupportScenario(run, polling, dialogs, caravanFixture, serverState.Ledger, eventFixture);
        var remoteMaps = new RemoteMapScenario(run, polling, colonyFixture);
        var world = new WorldScenario(run, polling, profile, serverState.WorldConfiguration, serverState.WorldTileDistanceCalculator,
            id => WorldPersistenceProbe.Read(root, id));
        var entry = new ServerEntryScenario(run, polling, dialogs, world, colonyFixture, diagnostics, () => url, serverState.OnlinePresence.IsUserOnline);
        var administration = new AdministrationScenario(run, polling, entry, serverState.AdminControl, serverState.WorldConfiguration);
        var passwords = new PasswordScenario(run, polling, serverState.OfflineAccounts);
        var chat = new ChatScenario(run, polling);
        var players = new PlayerScenarioFixture(run, polling);
        var diplomacy = new DiplomacyScenario(run, polling, players);
        var appearance = new ColonyAppearanceScenario(run, polling, serverState.WorldConfiguration);
        var giftScenario = new GiftScenario(run, polling, dialogs, colonyFixture, caravanFixture, eventFixture, serverState.Ledger, entry);
        var notifications = new NotificationScenario(run, polling, entry, eventFixture, serverState.Ledger);
        var roundtrips = new SessionPersistenceScenario(run, polling, colonyFixture, entry);
        var bank = new BankScenario(run, polling, colonyFixture, economy, mercenaries, roundtrips);
        var shopActions = new ShopScenarioFixture(run, polling);
        var shop = new ShopScenario(run, polling, colonyFixture, economy, shopActions);
        var shopCatalog = new ShopCatalogScenario(run, polling, colonyFixture, economy, shopActions, serverState.ServerShop, Path.Combine(root, "shop-catalog.json"));
        var shopStress = new ShopStressScenario(run, colonyFixture, shopActions, serverState.ServerShop,
            (id, buyers) => ShopPersistenceProbe.Read(root, id, buyers), Path.Combine(root, "shop-stress.json"));
        var raids = new RaidScenario(run, polling, dialogs, caravanFixture, remoteMaps, entry, colonyFixture, serverState.SnapshotStore, serverState.DiplomacyRelations, serverState.Ledger, eventFixture);
        var combat = new CombatScenario(run, polling, colonyFixture, remoteMaps, raids);
        var compatibility = new CompatibilityBaselineScenario(run, polling, entry, serverState.CompatibilityBaseline, () => url, serverState.OnlinePresence.IsUserOnline);
        var accountAuthority = new AccountLifecycleAuthority(serverState.SnapshotStore, serverState.Players, serverState.WorldConfiguration,
            serverState.BankLoans, serverState.MercenaryContracts, serverState.OfflineAccounts, serverState.Ledger);
        var accountColonies = new AccountColonyScenario(run, polling, colonyFixture, economy, achievements, entry, chat, players, shopActions, accountAuthority, () => url);
        var accounts = new AccountLifecycleScenario(run, polling, colonyFixture, economy, mercenaries, passwords, achievements, chat, shop, diplomacy, trades, tradeActions, accountColonies);
        var moves = new ColonyMoveScenarioFixture(run, polling, colonyFixture, economy, serverState.WorldConfiguration);
        var relocation = new RelocationScenario(run, moves);
        var gravship = new GravshipScenario(run, polling, dialogs, moves);
        var transfers = new PawnTransferScenario(caravanFixture, support, remoteMaps, diplomacy, raids);
        var featureRefresh = new FeatureRefreshScenario(run, economy);
        execution = plan.Bind(
            ScenarioCatalog.Startup.Bind(("launch-profile", () => world.VerifyStartup(clients.Keys)), ("shared-world-entry", entry.EnterSharedWorld)),
            world.CreateSequence(),
            ScenarioCatalog.Features.Bind(
                ("account-password-change", passwords.Change), ("colony-appearance-sync", appearance.Run), ("discover-players", players.DiscoverBoth),
                ("public-chat", chat.Public), ("private-chat-read", chat.Private), ("unicode-chat-blank-rejection", chat.UnicodeAndBlankRejection),
                ("admin-configuration-permissions", administration.Configuration), ("admin-action-permissions", administration.Guards),
                ("admin-role-grant-revoke", administration.Roles), ("admin-broadcast", administration.Broadcast),
                ("admin-kick-reconnect", administration.Kick), ("admin-ban-unban", administration.Ban), ("maintenance-password-rejection", administration.MaintenanceAndPassword),
                ("bank-input-rejection", bank.InputBounds), ("bank-loan-repayment", bank.LoanRepayment),
                ("shop-purchase-stock", shop.Purchase), ("shop-buyback-stock-rejection", shop.Buyback),
                ("trade-cancel-refund", trades.Cancel), ("trade-memo-cancel", trades.CancelMemo), ("trade-drop-pod-fulfillment", trades.Fulfill),
                ("achievement-unlock-leaderboard", achievements.ReachTradeThreshold), ("alliance-request-accept", diplomacy.Alliance),
                ("ally-observation-close", () => remoteMaps.ObserveAlly(false)), ("caravan-load-travel", caravanFixture.Travel),
                ("trade-caravan-fulfillment", trades.FulfillByCaravan), ("caravan-gift-accept", giftScenario.Accept), ("support-send-early-return", support.Run),
                ("mercenary-hire", mercenaries.Hire), ("mercenary-recall", mercenaries.Recall), ("raid-open-settle", () => raids.RunAndReconnect(false)),
                ("refresh-features", () => featureRefresh.Run(clients.Keys))),
            ScenarioCatalog.GiftReplay.Bind(("alliance-request-accept", diplomacy.Alliance), ("caravan-load-travel", caravanFixture.Travel),
                ("caravan-gift-accept", giftScenario.Accept), ("gift-repeat-accept", giftScenario.Replay)),
            ScenarioCatalog.Offline.Bind(("alliance-request-accept", diplomacy.Alliance), ("caravan-load-travel", caravanFixture.Travel),
                ("gift-offline-deferred-accept", giftScenario.OfflineAccept), ("gift-offline-reject-return", giftScenario.OfflineReject), ("notification-offline-policy", notifications.OfflineDelivery)),
            accounts.CreateSequence(),
            ScenarioCatalog.Bank.Bind(("bank-input-rejection", bank.InputBounds), ("bank-overdue-repayment", bank.OverdueRepayment),
                ("bank-mercenary-debt", bank.MercenaryDebt), ("mercenary-guard-purchase", mercenaries.GuardPurchase)),
            ScenarioCatalog.Combat.Bind(("baseline-entry-rejection", compatibility.RejectIncompatible), ("alliance-request-accept", diplomacy.Alliance),
                ("combat-fixture", combat.Prepare), ("remote-pawn-factions-ai", combat.Observe), ("caravan-load-travel", caravanFixture.Travel), ("raid-defense-points-ai", combat.Raid)),
            ScenarioCatalog.Relocation.Bind(("relocation-caravan", () => relocation.Run("A", "caravan")), ("relocation-quest-site", () => relocation.Run("B", "site"))),
            ScenarioCatalog.Gravship.Bind(("gravship-new-map", () => gravship.Run("A", false)), ("gravship-quest-site", () => gravship.Run("B", true))),
            transfers.CreateSequence(withRaid: true), transfers.CreateSequence(withRaid: false),
            ScenarioCatalog.Catalog.Bind(("shop-complete-catalog", () => shopCatalog.Run(profile.CatalogDefinitions))),
            ScenarioCatalog.CatalogSample.Bind(("shop-selected-catalog", () => shopCatalog.Run(profile.CatalogDefinitions))),
            ScenarioCatalog.Stress.Bind(("shop-large-data", shopStress.Run)), complexItems.CreateSequence(),
            ScenarioCatalog.Permanent.Bind(("alliance-request-accept", diplomacy.Alliance), ("caravan-load-travel", caravanFixture.Travel),
                ("support-permanent-state", () => support.Run("permanent", true))),
            ScenarioCatalog.Death.Bind(("alliance-request-accept", diplomacy.Alliance), ("caravan-load-travel", caravanFixture.Travel),
                ("support-death-expiry", () => support.Run("death", true))),
            ScenarioCatalog.Finish.Bind(("snapshot-save-reload", roundtrips.SaveReload), ("session-reconnect", roundtrips.Reconnect),
                ("runtime-errors", () => diagnostics.Verify(clients.Keys))));

    }
    public async Task<int> RunAsync()
    {
        bool passed = false;
        try
        {
            await server.StartAsync();
            url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await File.WriteAllTextAsync(Path.Combine(root, "server.json"), JsonSerializer.Serialize(new { url, data = Path.Combine(root, "Server", "Data") }, Wire.Json));
            clients["A"] = await RimTestClient.ConnectAsync(pipeA, TimeSpan.FromSeconds(120));
            clients["B"] = await RimTestClient.ConnectAsync(pipeB, TimeSpan.FromSeconds(120));
            foreach (var pair in clients) await run.AddClientAsync(pair.Key, pair.Value);
            foreach (var sequence in execution) await sequence.Run(run);
            passed = true;
        }
        catch (Exception ex)
        {
            await run.RecordAsync(new { failure = ex.ToString() }); Console.Error.WriteLine(ex);
            await run.CollectDiagnosticsAsync(["clash.state", "clash.events", "runtime.logs", "game.messages", "ui.windows"]);
        }
        finally
        {
            passed = await run.SaveReportAsync(passed, new { scope = "live-feature-matrix", fullCoverage = false, profile, coverageCatalog = "docs/rimtest-matrix.md" });
            foreach (var client in clients.Values) await client.DisposeAsync();
            await server.StopAsync(); await server.DisposeAsync();
        }
        Console.WriteLine(passed ? "PASS two-client game tests" : "FAIL two-client game tests");
        return passed ? 0 : 1;

    }
}
