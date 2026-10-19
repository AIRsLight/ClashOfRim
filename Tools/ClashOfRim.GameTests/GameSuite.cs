using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;


namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    private readonly string root, pipeA, pipeB;
    private readonly SuiteProfile profile;
    private readonly TestRun run;
    private readonly ScenarioDialogs dialogs;
    private readonly ScenarioPolling polling;
    private readonly ColonyScenarioFixture colonyFixture;
    private readonly EconomicScenarioFixture economy;
    private readonly MercenaryScenario mercenaries;
    private readonly BankScenario bank;
    private readonly AdministrationScenario administration;
    private readonly PasswordScenario passwords;
    private readonly ChatScenario chat;
    private readonly PlayerScenarioFixture players;
    private readonly DiplomacyScenario diplomacy;
    private readonly ColonyAppearanceScenario appearance;
    private readonly ShopScenario shop;
    private readonly TradeScenarioFixture tradeActions;
    private readonly TradeScenario trades;
    private readonly AchievementScenario achievements;
    private readonly ComplexItemScenario complexItems;
    private readonly ShopCatalogScenario shopCatalog;
    private readonly ShopStressScenario shopStress;
    private readonly CaravanScenario caravanFixture;
    private readonly GiftScenario giftScenario;
    private readonly SupportScenario support;
    private readonly RaidScenario raids;
    private readonly RemoteMapScenario remoteMaps;
    private readonly NotificationScenario notifications;
    private readonly EventScenarioFixture eventFixture;
    private readonly ScenarioRuntimeDiagnostics diagnostics;
    private readonly WorldScenario world;
    private readonly ServerEntryScenario entry;
    private readonly SessionPersistenceScenario roundtrips;
    private readonly Microsoft.AspNetCore.Builder.WebApplication server;
    private readonly Dictionary<string, RimTestClient> clients = new();
    private string url = "";
    private ClashOfRimNetworkState ServerState => server.Services.GetRequiredService<ClashOfRimNetworkState>();
    private static readonly string[] FeatureCases = ["account-password-change", "colony-appearance-sync", "public-chat", "private-chat-read", "admin-configuration-permissions",
    "bank-loan-repayment", "shop-purchase-stock", "trade-cancel-refund", "trade-drop-pod-fulfillment", "alliance-request-accept",
    "ally-observation-close", "caravan-load-travel", "caravan-gift-accept", "support-send-early-return", "mercenary-hire", "mercenary-recall", "raid-open-settle",
    "unicode-chat-blank-rejection", "admin-action-permissions", "admin-role-grant-revoke", "admin-broadcast", "bank-input-rejection", "shop-buyback-stock-rejection", "trade-memo-cancel", "achievement-unlock-leaderboard",
    "admin-kick-reconnect", "admin-ban-unban", "maintenance-password-rejection", "trade-caravan-fulfillment"];
    private static readonly string[] WorldCases = ["launch-profile", "shared-world-entry", "world-parameters", "world-geometry", "world-tile-data", "world-feature-identities",
    "storyteller-difficulty", "culture-references", "world-persistence", "storyteller-permission", "runtime-difficulty-sync", "snapshot-save-reload", "session-reconnect"];
    private static readonly string[] GiftReplayCases = ["alliance-request-accept", "caravan-load-travel", "caravan-gift-accept", "gift-repeat-accept"];
    public GameSuite(string root, string pipeA, string pipeB, SuiteProfile profile)
    {
        this.root = root; this.pipeA = pipeA; this.pipeB = pipeB; this.profile = profile;
        Directory.CreateDirectory(root);
        run = new TestRun(root, WorldCases.Concat(profile.Features ? FeatureCases : profile.GiftReplay ? GiftReplayCases : StabilityCases(profile.Stability)).Append("runtime-errors").ToArray());
        dialogs = new ScenarioDialogs(run, profile.A.Language != profile.B.Language);
        polling = new ScenarioPolling(run, dialogs);
        server = ClashOfRimNetworkServer.Build([
            "--contentRoot", Path.Combine(root, "Server"), "--urls", "http://127.0.0.1:0",
        "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Authentication:DebugMode", "false",
        "--Localization:Language", "English", "--Logging:LogLevel:Default", "Warning"]);
        colonyFixture = new(run, polling);
        diagnostics = new(run);
        var serverState = ServerState;
        economy = new(run, polling, colonyFixture);
        mercenaries = new(run, polling, colonyFixture, economy, serverState.MercenaryContracts, serverState.MercenaryGuards, () =>
        {
            var configuration = serverState.ServerConfiguration;
            return (configuration.MercenaryGuardApprenticeSilver, configuration.MercenaryGuardApprenticePointsRatio);
        });
        caravanFixture = new(run, polling, dialogs, colonyFixture);
        tradeActions = new(run, polling, dialogs);
        trades = new(run, polling, dialogs, colonyFixture, economy, caravanFixture, tradeActions);
        achievements = new(run, polling, trades, serverState.Achievements, serverState.SnapshotStore, serverState.Players);
        complexItems = new(run, polling, colonyFixture, tradeActions);
        eventFixture = new(polling, serverState.Ledger);
        support = new(run, polling, dialogs, caravanFixture, serverState.Ledger, eventFixture);
        remoteMaps = new(run, polling, colonyFixture);
        world = new(run, polling, profile, serverState.WorldConfiguration, serverState.WorldTileDistanceCalculator,
            id => WorldPersistenceProbe.Read(root, id));
        entry = new(run, polling, dialogs, world, colonyFixture, diagnostics, () => url, serverState.OnlinePresence.IsUserOnline);
        administration = new(run, polling, entry, serverState.AdminControl, serverState.WorldConfiguration);
        passwords = new(run, polling, serverState.OfflineAccounts);
        chat = new(run, polling);
        players = new(run, polling);
        diplomacy = new(run, polling, players);
        appearance = new(run, polling, serverState.WorldConfiguration);
        giftScenario = new(run, polling, dialogs, colonyFixture, caravanFixture, eventFixture, serverState.Ledger, entry);
        notifications = new(run, polling, entry, eventFixture, serverState.Ledger);
        roundtrips = new(run, polling, colonyFixture, entry);
        bank = new(run, polling, colonyFixture, economy, mercenaries, roundtrips);
        var shopActions = new ShopScenarioFixture(run, polling);
        shop = new(run, polling, colonyFixture, economy, shopActions);
        shopCatalog = new(run, polling, colonyFixture, economy, shopActions, serverState.ServerShop, Path.Combine(root, "shop-catalog.json"));
        shopStress = new(run, colonyFixture, shopActions, serverState.ServerShop,
            (id, buyers) => ShopPersistenceProbe.Read(root, id, buyers), Path.Combine(root, "shop-stress.json"));
        raids = new(run, polling, dialogs, caravanFixture, remoteMaps, entry, serverState.SnapshotStore, serverState.DiplomacyRelations, serverState.Ledger, eventFixture);
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
            await Case("launch-profile", async () =>
            {
                foreach (string client in clients.Keys)
                {
                    var hello = await Call(client, "runtime.hello"); Check(!hello.Bool("unsafeEnabled"), "Unsafe mode must remain off.");
                    await Wait(client, "game.state", null, s => s.Bool("startupReady") && !s.Bool("playing"));
                    world.VerifyLaunch(client, hello);
                    await Call(client, "clash.actions", new { limit = 200 });
                }
            });
            await Case("shared-world-entry", async () =>
            {
                await entry.Enter("A", url); await entry.Enter("B", url);
                await world.VerifySharedEntry();
            });
            await world.Run();
            if (profile.Features)
            {
                await Case("account-password-change", passwords.Change);
                await Case("colony-appearance-sync", appearance.Run);
                await players.Refresh("A"); await players.Refresh("B");
                await players.Observe("A", "rimtest-B");
                await Case("public-chat", chat.Public);
                await Case("private-chat-read", chat.Private);
                await Case("unicode-chat-blank-rejection", chat.UnicodeAndBlankRejection);
                await Case("admin-configuration-permissions", administration.Configuration);
                await Case("admin-action-permissions", administration.Guards);
                await Case("admin-role-grant-revoke", administration.Roles);
                await Case("admin-broadcast", administration.Broadcast);
                await Case("admin-kick-reconnect", administration.Kick);
                await Case("admin-ban-unban", administration.Ban);
                await Case("maintenance-password-rejection", administration.MaintenanceAndPassword);
                await Case("bank-input-rejection", bank.InputBounds);
                await Case("bank-loan-repayment", bank.LoanRepayment);
                await Case("shop-purchase-stock", shop.Purchase);
                await Case("shop-buyback-stock-rejection", shop.Buyback);
                await Case("trade-cancel-refund", trades.Cancel);
                await Case("trade-memo-cancel", trades.CancelMemo);
                await Case("trade-drop-pod-fulfillment", trades.Fulfill);
                await Case("achievement-unlock-leaderboard", achievements.ReachTradeThreshold);
                await Case("alliance-request-accept", diplomacy.Alliance);
                await Case("ally-observation-close", () => remoteMaps.ObserveAlly(profile.Stability == "transfers"));
                await Case("caravan-load-travel", caravanFixture.Travel);
                await Case("trade-caravan-fulfillment", trades.FulfillByCaravan);
                await Case("caravan-gift-accept", giftScenario.Accept);
                await Case("support-send-early-return", support.Run);
                await Case("mercenary-hire", mercenaries.Hire);
                await Case("mercenary-recall", mercenaries.Recall);
                await Case("raid-open-settle", async () => { await raids.Run(profile.Stability == "transfers"); await entry.LoginExisting(true); });
                foreach (string client in clients.Keys)
                {
                    if (!(await Call(client, "clash.state")).Bool("isInActiveMultiplayerSession")) continue;
                    await economy.RefreshBank(client);
                    await Invoke(client, "StartRefreshAchievements"); await Invoke(client, "StartRefreshTradeOrders");
                    await Invoke(client, "StartRefreshServerShop"); await Invoke(client, "StartSyncWorldMapMarkers");
                    await Call(client, "clash.records", new { kind = "markers" });
                }
            }
            if (profile.GiftReplay)
            {
                await Case("alliance-request-accept", diplomacy.Alliance);
                await Case("caravan-load-travel", caravanFixture.Travel);
                await Case("caravan-gift-accept", giftScenario.Accept);
                await Case("gift-repeat-accept", giftScenario.Replay);
            }
            await RunStability();
            // Reload tests run last: a broken serialized world must not prevent observing
            // independent live business features, or be uploaded as if it were valid.
            await Case("snapshot-save-reload", roundtrips.SaveReload);
            await Case("session-reconnect", roundtrips.Reconnect);
            await Case("runtime-errors", () => diagnostics.Verify(clients.Keys));
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
    Task<JsonElement> Wait(string client, string method, object? input, Func<JsonElement, bool> predicate, int seconds = 90)
        => polling.Wait(client, method, input, predicate, seconds);
    Task Confirm(string client) => dialogs.Confirm(client);
    Task<bool> AcceptLanguageWarning(string client) => dialogs.AcceptLanguageWarning(client);
    Task<JsonElement> Idle(string client) => polling.Idle(client);
    Task Step(string client, int ticks) => polling.Step(client, ticks);
    Task<JsonElement> Call(string client, string method, object? input = null) => run.CallAsync(client, method, input);
    Task<JsonElement> Invoke(string client, string action, object? input = null) => run.InvokeClientAsync(client, action, input);
    Task Case(string id, Func<Task> body) => run.RunCaseAsync(id, body, CaptureLogs);
    Task CaptureLogs() => run.CaptureLogsAsync();
}
