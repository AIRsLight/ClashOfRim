using System.Diagnostics;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ServerEntryScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs,
    WorldScenario world, ColonyScenarioFixture colony, ScenarioRuntimeDiagnostics diagnostics, Func<string> serverAddress, Func<string, bool> isUserOnline)
{
    private int? firstColonyTile;
    internal async Task EnterSharedWorld()
    {
        string address = serverAddress();
        await Enter("A", address); await Enter("B", address);
        await world.VerifySharedEntry();
    }
    internal void SetNeighbouringColony(int tile) => firstColonyTile = tile;
    internal async Task Enter(string client, string url, bool register = true)
    {
        Console.WriteLine("ENTER " + client);
        // Capture immediately before this client's first action. The other client may
        // have finished asynchronous startup while we created the first colony.
        await diagnostics.CaptureStartup(client);
        await run.InvokeClientAsync(client, "StartMainMenuServerEntryFlow", new { serverBaseUrl = url, userId = "rimtest-" + client, password = "local-test-password", createAccountIfMissing = register });
        var watch = Stopwatch.StartNew(); var visited = new HashSet<string>(); int nextProgress = 15, entryLogCursor = 0;
        while (watch.Elapsed < TimeSpan.FromMinutes(6))
        {
            var state = await run.CallAsync(client, "game.state");
            if (watch.Elapsed.TotalSeconds > nextProgress)
            {
                nextProgress += 15;
                Console.WriteLine(client + " setup " + state.GetRawText());
                Console.WriteLine(client + " session " + (await run.CallAsync(client, "clash.state")).GetRawText());
            }
            if (state.Bool("playerHasControl") && state.Int("mapCount") > 0) break;
            await dialogs.Confirm(client);
            var pages = Items(await run.CallAsync(client, "pages.list"));
            if (pages.Length == 0)
            {
                if (await dialogs.AcceptLanguageWarning(client)) continue;
                var entries = Items(await run.CallAsync(client, "runtime.logs", new { after = entryLogCursor, limit = 200 }));
                foreach (var entry in entries)
                {
                    entryLogCursor = Math.Max(entryLogCursor, entry.Int("sequence"));
                    diagnostics.CheckEntry(client, entry);
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
                    await world.SelectStoryteller(client, handle); break;
                case "RimWorld.Page_CreateWorldParams":
                    await world.ConfigureWorld(client, handle); break;
                case "RimWorld.Page_SelectStartingSite":
                    await world.SelectSite(client, handle, firstColonyTile); break;
                case "RimWorld.Page_ConfigureStartingPawns": break;
                case "RimWorld.Page_ChooseIdeoPreset": break;
                default: throw new Exception("Unhandled setup page: " + type);
            }
            await run.CallAsync(client, "page.next", new { page = handle });
        }
        await polling.Wait(client, "game.state", null, s => s.Bool("playerHasControl") && s.Int("mapCount") > 0);
        await dialogs.Confirm(client);
        await polling.Wait(client, "clash.state", null, s => s.Bool("isInActiveMultiplayerSession") && !string.IsNullOrEmpty(s.OptionalString("currentSnapshotId")) && !s.Bool("snapshotUploadInProgress"));
        foreach (var dialog in Items(await run.CallAsync(client, "dialogs.nodes")))
        {
            // Only the single-choice vanilla arrival acknowledgment is expected at this point.
            var options = dialog.GetProperty("options").EnumerateArray().ToArray();
            Check(options.Length == 1 && options[0].Bool("closes"), "Unexpected interactive node dialog during arrival.");
            if (!dialog.Bool("interactive")) await Task.Delay(1100);
            await run.CallAsync(client, "dialog.choose", new { dialog = H(dialog), node = dialog.GetProperty("node").String("handle"), option = H(options[0]) });
        }
        var home = await colony.Stabilize(client);
        firstColonyTile ??= home.Int("tileId");
        Check(home.GetProperty("colonists").GetArrayLength() == 3, "Expected three landed crashlanded colonists.");
        Console.WriteLine("READY " + client);
    }
    internal async Task ExitSession(string client)
    {
        // The product logout is an exit hook. Leaving the loaded game ticking after
        // calling the hook alone lets its component reconnect, unlike a real exit.
        await run.CallAsync(client, "game.returnToMenu", new { discardUnsavedChanges = true });
        await polling.Wait(client, "game.state", null, s => s.Bool("startupReady") && !s.Bool("hasGame") && !s.Bool("playing"));
        await polling.Wait(client, "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
    }
    internal async Task LoginExisting(bool accepted, string password = "local-test-password", string client = "B")
    {
        await polling.Idle(client);
        string previousEpoch = (await run.CallAsync(client, "game.state")).String("epoch");
        Check((await run.InvokeClientAsync(client, "StartMainMenuServerEntryFlow", new { serverBaseUrl = serverAddress(), userId = "rimtest-" + client, password, createAccountIfMissing = false })).Bool("returnValue"), "Login entry was not started.");
        await polling.Wait(client, "clash.state", null, s => !s.Bool("manualSyncInProgress") && s.Bool("isInActiveMultiplayerSession") == accepted, 120);
        if (accepted)
        {
            // Authentication completes before the downloaded snapshot replaces the
            // old game. Do not mistake the still-visible old map for a finished login.
            await polling.Wait(client, "game.state", null, s => s.String("epoch") != previousEpoch && s.Bool("playing") &&
                !s.Bool("longEventPending") && s.Bool("playerHasControl") && s.Int("mapCount") > 0, 180);
            await colony.Stabilize(client);
            await polling.Idle(client);
        }
    }
    internal async Task LogoutWithSnapshot(string client)
    {
        await polling.Idle(client); await run.InvokeClientAsync(client, "StartManualSnapshotUpload"); await polling.Idle(client);
        await ExitSession(client);
        // The menu becomes ready before the asynchronous logout reaches the server.
        var timer = Stopwatch.StartNew();
        while (isUserOnline("rimtest-" + client) && timer.Elapsed.TotalSeconds < 30)
            await Task.Delay(100);
        Check(!isUserOnline("rimtest-" + client), "Logout left server presence online.");
    }

}
