using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class UiLayoutScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, string root)
{
    internal async Task Run()
    {
        await run.InvokeClientAsync("A", "StartAdminAction", new { actionKind = "PromoteAdmin", targetUserId = "rimtest-B" });
        await polling.Idle("A");
        await polling.Wait("B", "clash.state", null, s => s.Bool("isAdministrator"));
        var shop = new ShopScenarioFixture(run, polling);
        foreach (string definition in new[] { "Steel", "Gun_AssaultRifle", "BionicArm", "MedicineIndustrial", "ComponentIndustrial", "Apparel_Parka" })
        {
            var item = await run.CallAsync("A", "clash.shop.template", new { defName = definition, count = 1 });
            await shop.Upsert("A", null, "SellToPlayer", item.GetProperty("reference"), 1234567, 999999);
        }
        var samples = new List<object>(); var failures = new List<string>();
        foreach (string client in new[] { "A", "B" })
        {
            await colony.PauseWhenIdle(client);
            foreach (string action in new[] { "StartRefreshPlayers", "StartRefreshBankStatus", "StartRefreshAdminStatus", "StartRefreshAchievements", "StartRefreshTradeOrders", "StartRefreshServerShop" })
            {
                await run.InvokeClientAsync(client, action); await polling.Idle(client);
            }
            await polling.Wait(client, "clash.records", new { kind = "shop" }, s => Items(s).Count() == 6);
            foreach (var display in new[] { (1280, 720, 1f), (1920, 1080, 1f), (1920, 1080, 1.25f) })
            {
                await run.CallAsync(client, "clash.ui.configure", new { acknowledgeFixture = true, width = display.Item1, height = display.Item2, scale = display.Item3 });
                await Task.Delay(400);
                foreach (string target in Targets)
                {
                    var opened = await run.CallAsync(client, "clash.ui.open", new { acknowledgeFixture = true, target });
                    var state = await polling.Wait(client, "clash.ui.read", null, s => s.Int("frame") > opened.Int("frame") + 2, seconds: 15);
                    await Task.Delay(250);
                    state = await run.CallAsync(client, "clash.ui.read");
                    Check(state.GetProperty("labels").GetArrayLength() > 0, "No actual GUI labels observed: " + target);
                    var overflow = state.GetProperty("labels").EnumerateArray().Where(l => l.Bool("overflow")).ToArray();
                    string name = client + "-" + display.Item1 + "x" + display.Item2 + "-" + display.Item3.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '-') + "-" + target;
                    var capture = await run.CallAsync(client, "clash.ui.capture", new { acknowledgeFixture = true, name });
                    string path = capture.String("path");
                    for (int attempt = 0; attempt < 30 && !File.Exists(path); attempt++) await Task.Delay(100);
                    Check(File.Exists(path) && new FileInfo(path).Length > 1024, "Rendered screenshot missing: " + name);
                    samples.Add(new { target, client, display = new { width = display.Item1, height = display.Item2, scale = display.Item3 }, state, screenshot = capture.String("path") });
                    if (overflow.Length > 0) failures.Add(name + ": " + overflow.Length + " overflowing labels");
                    await File.WriteAllTextAsync(Path.Combine(root, "ui-layout.json"), JsonSerializer.Serialize(samples, Wire.Json));
                }
            }
            await run.CallAsync(client, "clash.ui.open", new { acknowledgeFixture = true, target = "close" });
        }
        Console.WriteLine("UI layout samples: " + samples.Count + "; overflowing surfaces: " + failures.Count);
        Check(failures.Count == 0, string.Join("\n", failures));
    }
    private static readonly string[] Targets = new[] { "Trade", "Shop", "Bank", "Mercenary", "Diplomacy", "Achievements", "Account", "Diagnostics", "Admin" }.Select(t => "main-" + t)
        .Concat(new[] { "Trade", "Shop", "Bank", "Mercenary", "Raid", "Players", "Maintenance", "Manifest", "Audit" }.Select(t => "admin-" + t))
        .Concat(new[] { "trade-create", "trade-market", "shop-edit", "support-duration", "support-request", "abandon", "relocation", "entry", "registration", "progress", "session-expired", "snapshot-failure", "select-item", "select-incident", "select-research", "shop-sell", "shop-buy", "shop-buy-bottom", "appearance", "baseline-progress", "select-trait", "select-genes", "edit-genes", "compatibility-Overview", "compatibility-Manifest", "compatibility-Hash", "compatibility-Config" }).ToArray();
}
