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
    private readonly Microsoft.AspNetCore.Builder.WebApplication server;
    private readonly Dictionary<string, HashSet<int>> startupErrors = new();
    private readonly Dictionary<string, RimTestClient> clients = new();
    private string? expedition;
    private string? acceptedGiftId;
    private int? firstColonyTile;
    private string url = "";
    private JsonElement expectedDifficulty, expectedWorld;
    private JsonElement worldA, worldB;
    private static readonly string[] FeatureCases = ["account-password-change", "colony-appearance-sync", "public-chat", "private-chat-read", "admin-configuration-permissions",
    "bank-loan-repayment", "shop-purchase-stock", "trade-cancel-refund", "trade-drop-pod-fulfillment", "alliance-request-accept",
    "ally-observation-close", "caravan-load-travel", "caravan-gift-accept", "support-send-early-return", "mercenary-hire", "mercenary-recall", "raid-open-settle",
    "unicode-chat-blank-rejection", "admin-action-permissions", "admin-role-grant-revoke", "admin-broadcast", "bank-input-rejection", "shop-buyback-stock-rejection", "trade-memo-cancel", "achievement-unlock-leaderboard",
    "admin-kick-reconnect", "admin-ban-unban", "maintenance-password-rejection"];
    private static readonly string[] WorldCases = ["launch-profile", "shared-world-entry", "world-parameters", "world-geometry", "world-tile-data", "world-feature-identities",
    "storyteller-difficulty", "culture-references", "world-persistence", "storyteller-permission", "runtime-difficulty-sync", "snapshot-save-reload", "session-reconnect"];
    private static readonly string[] GiftReplayCases = ["alliance-request-accept", "caravan-load-travel", "caravan-gift-accept", "gift-repeat-accept"];
    public GameSuite(string root, string pipeA, string pipeB, SuiteProfile profile)
    {
        this.root = root; this.pipeA = pipeA; this.pipeB = pipeB; this.profile = profile;
        Directory.CreateDirectory(root);
        run = new TestRun(root, WorldCases.Concat(profile.Features ? FeatureCases : profile.GiftReplay ? GiftReplayCases : StabilityCases(profile.Stability)).Append("runtime-errors").ToArray());
        server = ClashOfRimNetworkServer.Build([
            "--contentRoot", Path.Combine(root, "Server"), "--urls", "http://127.0.0.1:0",
        "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Authentication:DebugMode", "false",
        "--Localization:Language", "English", "--Logging:LogLevel:Default", "Warning"]);
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
                    VerifyLaunch(client, hello);
                    await Call(client, "clash.actions", new { limit = 200 });
                }
            });
            await Case("shared-world-entry", async () =>
            {
                await Enter("A", url); await Enter("B", url);
                Check(server.Services.GetRequiredService<ClashOfRimNetworkState>().WorldConfiguration.Current is not null, "No persisted server world baseline.");
                var worldA = await Call("A", "clash.world"); var worldB = await Call("B", "clash.world");
                Check(!string.IsNullOrWhiteSpace(worldA.OptionalString("worldConfigurationId")) && worldA.GetRawText() == worldB.GetRawText(), "Clients did not apply the same authoritative world identity/topology.");
                var a = (await Call("A", "clash.inventory")).GetProperty("maps")[0];
                var b = (await Call("B", "clash.inventory")).GetProperty("maps")[0];
                Check(a.Int("tileId") != b.Int("tileId"), "Clients occupied the same colony tile.");
                var sites = server.Services.GetRequiredService<ClashOfRimNetworkState>().WorldConfiguration.Current!.PlayerColonySites;
                Check(sites.Any(s => s.UserId == "rimtest-A" && s.Tile == a.Int("tileId")) &&
                    sites.Any(s => s.UserId == "rimtest-B" && s.Tile == b.Int("tileId")), "Both colony sites must be registered authoritatively.");
            });
            await WorldChecks();
            if (profile.Features)
            {
                await Case("account-password-change", PasswordChange);
                await Case("colony-appearance-sync", Appearance);
                await Invoke("A", "StartRefreshPlayers"); await Invoke("B", "StartRefreshPlayers");
                await Wait("A", "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == "rimtest-B"));
                await Case("public-chat", async () =>
                {
                    await Invoke("A", "StartSendChatMessage", new { rawText = "RimTest public exchange" });
                    await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
                    await Invoke("B", "StartRefreshChatMessages");
                    await Wait("B", "clash.records", new { kind = "chat" }, p => Items(p).Any(v => v.GetProperty("data").GetRawText().Contains("RimTest public exchange")));
                });
                await Case("private-chat-read", PrivateChat);
                await Case("unicode-chat-blank-rejection", UnicodeChat);
                await Case("admin-configuration-permissions", AdminConfiguration);
                await Case("admin-action-permissions", AdminGuards);
                await Case("admin-role-grant-revoke", AdminRoles);
                await Case("admin-broadcast", AdminBroadcast);
                await Case("admin-kick-reconnect", AdminKick);
                await Case("admin-ban-unban", AdminBan);
                await Case("maintenance-password-rejection", MaintenanceAndPassword);
                await Case("bank-input-rejection", BankBounds);
                await Case("bank-loan-repayment", Bank);
                await Case("shop-purchase-stock", Shop);
                await Case("shop-buyback-stock-rejection", ShopBuyback);
                await Case("trade-cancel-refund", TradeCancel);
                await Case("trade-memo-cancel", TradeMemo);
                await Case("trade-drop-pod-fulfillment", TradeFulfill);
                await Case("achievement-unlock-leaderboard", Achievements);
                await Case("alliance-request-accept", Alliance);
                await Case("ally-observation-close", ObserveAlly);
                await Case("caravan-load-travel", Travel);
                await Case("caravan-gift-accept", Gift);
                await Case("support-send-early-return", Support);
                await Case("mercenary-hire", Mercenary);
                await Case("mercenary-recall", MercenaryRecall);
                await Case("raid-open-settle", async () => { await Raid(); await LoginExisting(true); });
                foreach (string client in clients.Keys)
                {
                    if (!(await Call(client, "clash.state")).Bool("isInActiveMultiplayerSession")) continue;
                    await Invoke(client, "StartRefreshBankStatus"); await Wait(client, "clash.state", null, s => !s.Bool("bankInProgress")); await Call(client, "clash.bank");
                    await Invoke(client, "StartRefreshAchievements"); await Invoke(client, "StartRefreshTradeOrders");
                    await Invoke(client, "StartRefreshServerShop"); await Invoke(client, "StartSyncWorldMapMarkers");
                    await Call(client, "clash.records", new { kind = "markers" });
                }
            }
            if (profile.GiftReplay)
            {
                await Case("alliance-request-accept", Alliance);
                await Case("caravan-load-travel", Travel);
                await Case("caravan-gift-accept", Gift);
                await Case("gift-repeat-accept", GiftReplay);
            }
            await RunStability();
            // Reload tests run last: a broken serialized world must not prevent observing
            // independent live business features, or be uploaded as if it were valid.
            await Case("snapshot-save-reload", SaveReload);
            await Case("session-reconnect", Reconnect);
            await Case("runtime-errors", async () =>
            {
                await CaptureLogs();
                foreach (string client in clients.Keys)
                {
                    var errors = run.Logs.Where(e => e.Client == client).Select(e => e.Entry).Where(e =>
                        (e.String("level") is "error" or "bridge" or "probe" && !startupErrors[client].Contains(e.Int("sequence"))) ||
                        e.String("message").Contains("[ClashOfRim] Main thread action failed:") ||
                        e.String("message").Contains("[ClashOfRim][AutoEventApply] prepare failed")).ToArray();
                    Check(errors.Length == 0, "Unexpected game errors: " + JsonSerializer.Serialize(errors));
                }
            });
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
