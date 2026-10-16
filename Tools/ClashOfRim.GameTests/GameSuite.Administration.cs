using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task ExitSession(string client)
    {
        // The product logout is an exit hook. Leaving the loaded game ticking after
        // calling the hook alone lets its component reconnect, unlike a real exit.
        await Call(client, "game.returnToMenu", new { discardUnsavedChanges = true });
        await Wait(client, "game.state", null, s => s.Bool("startupReady") && !s.Bool("hasGame") && !s.Bool("playing"));
        await Wait(client, "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
    }
    async Task LoginExisting(bool accepted, string password = "local-test-password")
    {
        await Idle("B");
        string previousEpoch = (await Call("B", "game.state")).String("epoch");
        Check((await Invoke("B", "StartMainMenuServerEntryFlow", new { serverBaseUrl = url, userId = "rimtest-B", password, createAccountIfMissing = false })).Bool("returnValue"), "Login entry was not started.");
        await Wait("B", "clash.state", null, s => !s.Bool("manualSyncInProgress") && s.Bool("isInActiveMultiplayerSession") == accepted, 120);
        if (accepted)
        {
            // Authentication completes before the downloaded snapshot replaces the
            // old game. Do not mistake the still-visible old map for a finished login.
            await Wait("B", "game.state", null, s => s.String("epoch") != previousEpoch && s.Bool("playing") &&
                !s.Bool("longEventPending") && s.Bool("playerHasControl") && s.Int("mapCount") > 0, 180);
            await StabilizeColony("B");
            await Idle("B");
        }
    }
    async Task AdminKick()
    {
        Check((await Call("B", "clash.state")).Bool("isInActiveMultiplayerSession"), "Guest must be online before kick.");
        await Invoke("A", "StartAdminAction", new { actionKind = "Kick", targetUserId = "rimtest-B" }); await Idle("A");
        await Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
        await LoginExisting(true);
        Check((await Call("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Kicking guest affected owner session.");
    }
    async Task AdminBan()
    {
        await Invoke("A", "StartAdminAction", new { actionKind = "Ban", targetUserId = "rimtest-B" }); await Idle("A");
        try
        {
            Check(ServerState.AdminControl.IsBanned("rimtest-B"), "Ban was not persisted.");
            await Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
            await LoginExisting(false);
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "Unban", targetUserId = "rimtest-B" }); await Idle("A"); }
        Check(!ServerState.AdminControl.IsBanned("rimtest-B"), "Unban was not persisted.");
        await LoginExisting(true);
    }
    async Task MaintenanceAndPassword()
    {
        await Invoke("B", "StartManualSnapshotUpload"); await Idle("B");
        await ExitSession("B");
        await LoginExisting(false, "incorrect-password");
        await Invoke("A", "StartAdminAction", new { actionKind = "LockMaintenance", message = "RimTest maintenance" }); await Idle("A");
        try
        {
            Check(ServerState.AdminControl.MaintenanceLoginLocked, "Maintenance lock not persisted.");
            await LoginExisting(false);
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "UnlockMaintenance" }); await Idle("A"); }
        Check(!ServerState.AdminControl.MaintenanceLoginLocked, "Maintenance unlock not persisted.");
        await LoginExisting(true);
    }
}
