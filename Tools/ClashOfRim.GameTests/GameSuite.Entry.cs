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
    static JsonElement[] Items(JsonElement p) => p.GetProperty("items").EnumerateArray().ToArray();
    static string H(JsonElement v) => v.GetProperty("reference").String("handle");
    static void Check(bool ok, string reason) => RimTest.Testing.Check.That(ok, reason);
    Task<JsonElement> Call(string client, string method, object? input = null) => run.CallAsync(client, method, input);
    WaitOptions Waiting(string client, string method, int seconds = 90) => new(TimeSpan.FromSeconds(seconds), AfterUnmet: async (value, token) =>
    {
        if (method == "clash.state") await AcceptLanguageWarning(client);
        // The flight ceremony opens the original abandon-map warning after ticking.
        // Resolve it explicitly and retain the dialog/button evidence in the trace.
        if (method == "clash.gravship") await Confirm(client);
        // This suite explicitly resumes ordinary event pauses, while forced modal pauses remain effective.
        // RimTest itself never makes this gameplay-policy decision.
        var ticking = method == "time.status" ? value : await run.CallAsync(client, "time.status", token: token);
        if (ticking.OptionalString("state") == "running" && ticking.Bool("paused") && !ticking.Bool("forcedPause"))
            await run.CallAsync(client, "time.resume", token: token);
    });
    Task<JsonElement> Wait(string client, string method, object? input, Func<JsonElement, bool> predicate, int seconds = 90) =>
        run.EventuallyAsync(client, method, input, predicate, Waiting(client, method, seconds));
    Task<JsonElement> Invoke(string client, string action, object? input = null) => Call(client, "clash.invoke", new { action, arguments = input ?? new { } });
    async Task Confirm(string client)
    {
        foreach (var dialog in Items(await Call(client, "dialogs.messages")))
            await Call(client, "dialogs.choose", new { dialog = H(dialog), button = "A" });
    }
    async Task Enter(string client, string url)
    {
        Console.WriteLine("ENTER " + client);
        // Capture immediately before this client's first action. The other client may
        // have finished asynchronous startup while we created the first colony.
        var hello = await Call(client, "runtime.hello");
        startupErrors[client] = Items(await Call(client, "runtime.logs", new { limit = 200 })).Where(e => hello.Bool("headless") && e.String("level") == "error" &&
            e.String("message") == "Resolution too small (0x0), resetting UI scale to 1.").Select(e => e.Int("sequence")).ToHashSet();
        await Invoke(client, "StartMainMenuServerEntryFlow", new { serverBaseUrl = url, userId = "rimtest-" + client, password = "local-test-password", createAccountIfMissing = true });
        var watch = Stopwatch.StartNew(); var visited = new HashSet<string>(); int nextProgress = 15, entryLogCursor = 0;
        while (watch.Elapsed < TimeSpan.FromMinutes(6))
        {
            var state = await Call(client, "game.state");
            if (watch.Elapsed.TotalSeconds > nextProgress)
            {
                nextProgress += 15;
                Console.WriteLine(client + " setup " + state.GetRawText());
                Console.WriteLine(client + " session " + (await Call(client, "clash.state")).GetRawText());
            }
            if (state.Bool("playerHasControl") && state.Int("mapCount") > 0) break;
            await Confirm(client);
            var pages = Items(await Call(client, "pages.list"));
            if (pages.Length == 0)
            {
                if (await AcceptLanguageWarning(client)) continue;
                var entries = Items(await Call(client, "runtime.logs", new { after = entryLogCursor, limit = 200 }));
                foreach (var entry in entries)
                {
                    entryLogCursor = Math.Max(entryLogCursor, entry.Int("sequence"));
                    Check(entry.String("level") != "error" || startupErrors[client].Contains(entry.Int("sequence")),
                        "Game error during entry: " + entry.String("message"));
                }
                await Task.Delay(250); continue;
            }
            var page = pages[^1]; string handle = H(page), type = page.GetProperty("reference").String("type");
            if (!visited.Add(handle)) { await Task.Delay(250); continue; }
            Console.WriteLine(client + " " + type);
            switch (type)
            {
                case "RimWorld.Page_SelectScenario": break;
                case "RimWorld.Page_SelectStoryteller":
                    var selected = await Call(client, "setup.storyteller", new
                    {
                        page = handle,
                        storyteller = profile.Storyteller,
                        difficulty = profile.Difficulty,
                        baseDifficulty = profile.BaseDifficulty,
                        values = profile.CustomValues,
                        permadeath = false
                    });
                    if (client == "A") expectedDifficulty = selected.GetProperty("values");
                    break;
                case "RimWorld.Page_CreateWorldParams":
                    await ConfigureWorld(client, handle); break;
                case "RimWorld.Page_SelectStartingSite":
                    object siteQuery = firstColonyTile is int origin
                        ? new { page = handle, biome = "TemperateForest", limit = 200, nearTileId = origin, maxDistance = 8 }
                        : new { page = handle, biome = "TemperateForest", limit = 200 };
                    var sites = Items(await Call(client, "setup.sites", siteQuery));
                    if (sites.Length == 0 && !profile.Features)
                    {
                        // Extreme climates need not contain temperate forest. Site validity
                        // still comes from the original settlement validator.
                        siteQuery = firstColonyTile is int near
                            ? new { page = handle, limit = 200, nearTileId = near, maxDistance = 8 }
                            : new { page = handle, limit = 200 };
                        sites = Items(await Call(client, "setup.sites", siteQuery));
                    }
                    if (firstColonyTile is int firstTile)
                    {
                        // Vanilla graph adjacency and the server's geometry estimate can
                        // differ. Select a site accepted by both; never bypass registration.
                        var serverState = server.Services.GetRequiredService<ClashOfRimNetworkState>();
                        var geometry = serverState.WorldConfiguration.Current!.TileGeometry!;
                        var source = new WorldTileGeometryDistanceSource(geometry.Layers.Select(l => new WorldTileLayerGeometry(
                            l.LayerId, l.AverageTileSize, l.TileCenters.Select(c => new WorldTileCenter(c.Tile, c.X, c.Y, c.Z)))));
                        sites = sites.Where(s => serverState.WorldTileDistanceCalculator.TryCalculateDistance(source,
                            new WorldTileRef(firstTile, s.Int("layerId")), new WorldTileRef(s.Int("tileId"), s.Int("layerId")), 0) is >= 2).ToArray();
                    }
                    Check(sites.Length > 0, "No valid temperate settlement site.");
                    await Call(client, "setup.site", new { page = handle, tileId = sites[0].Int("tileId") }); break;
                case "RimWorld.Page_ConfigureStartingPawns": break;
                case "RimWorld.Page_ChooseIdeoPreset": break;
                default: throw new Exception("Unhandled setup page: " + type);
            }
            await Call(client, "page.next", new { page = handle });
        }
        await Wait(client, "game.state", null, s => s.Bool("playerHasControl") && s.Int("mapCount") > 0);
        await Confirm(client);
        await Wait(client, "clash.state", null, s => s.Bool("isInActiveMultiplayerSession") && !string.IsNullOrEmpty(s.OptionalString("currentSnapshotId")) && !s.Bool("snapshotUploadInProgress"));
        foreach (var dialog in Items(await Call(client, "dialogs.nodes")))
        {
            // Only the single-choice vanilla arrival acknowledgment is expected at this point.
            var options = dialog.GetProperty("options").EnumerateArray().ToArray();
            Check(options.Length == 1 && options[0].Bool("closes"), "Unexpected interactive node dialog during arrival.");
            if (!dialog.Bool("interactive")) await Task.Delay(1100);
            await Call(client, "dialog.choose", new { dialog = H(dialog), node = dialog.GetProperty("node").String("handle"), option = H(options[0]) });
        }
        var home = await StabilizeColony(client);
        firstColonyTile ??= home.Int("tileId");
        Check(home.GetProperty("colonists").GetArrayLength() == 3, "Expected three landed crashlanded colonists.");
        Console.WriteLine("READY " + client);
    }
    async Task<JsonElement> StabilizeColony(string client)
    {
        await Call(client, "time.set", new { speed = "Paused" });
        // An authoritative initial snapshot can still contain unopened starting pods.
        // Use the same settling/drafting fixture after entry and snapshot restores.
        await Step(client, 600);
        var home = (await Call(client, "clash.inventory")).GetProperty("maps")[0];
        foreach (var pawn in home.GetProperty("colonists").EnumerateArray())
            await Call(client, "pawn.draft", new { pawn = H(pawn), drafted = true });
        return home;
    }
    async Task<bool> AcceptLanguageWarning(string client)
    {
        var warning = await Call(client, "clash.compatibility");
        if (!warning.Bool("open")) return false;
        Check(profile.A.Language != profile.B.Language && warning.Bool("canContinue"), "Unexpected compatibility mismatch; do not bypass it.");
        await Call(client, "clash.compatibility.continue");
        return true;
    }
    Task Case(string id, Func<Task> body) => run.RunCaseAsync(id, body, CaptureLogs);
    Task CaptureLogs() => run.CaptureLogsAsync();
    Task<JsonElement> Idle(string client) => Wait(client, "clash.state", null, s =>
        !s.Bool("manualSyncInProgress") && !s.Bool("snapshotUploadInProgress") && !s.Bool("localAtomicMutationPending") && !s.Bool("bankInProgress") && !s.Bool("serverShopInProgress") && !s.Bool("mercenaryInProgress") && !s.Bool("adminInProgress") && !s.Bool("tradeOrdersPageLoadInProgress"));
    async Task<int> Stock(string client, string def)
    {
        var inventory = await Call(client, "clash.inventory");
        return inventory.GetProperty("maps").EnumerateArray().Sum(m => m.GetProperty("items").TryGetProperty(def, out var v) ? v.GetInt32() : 0);
    }
    async Task Step(string client, int ticks)
    {
        var operation = await run.RunOperationAsync(client, "time.step", new { ticks, tps = 1200 }, "time.status", null,
            (accepted, current) => current.OptionalString("id") == accepted.String("id") && current.String("state") == "completed",
            Waiting(client, "time.status"), failed: (accepted, current) => current.OptionalString("id") == accepted.String("id") && current.String("state") is "cancelled" or "failed" ? "Tick operation stopped: " + current.GetRawText() : null);
        RimTest.Testing.Check.Equal(ticks, operation.LastObservation.Int("advancedTicks"), "Tick budget did not complete");
    }

}
