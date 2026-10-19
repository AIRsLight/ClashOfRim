using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task AdminKick()
    {
        Check((await Call("B", "clash.state")).Bool("isInActiveMultiplayerSession"), "Guest must be online before kick.");
        await Invoke("A", "StartAdminAction", new { actionKind = "Kick", targetUserId = "rimtest-B" }); await Idle("A");
        await Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
        await entry.LoginExisting(true);
        Check((await Call("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Kicking guest affected owner session.");
    }
    async Task AdminBan()
    {
        await Invoke("A", "StartAdminAction", new { actionKind = "Ban", targetUserId = "rimtest-B" }); await Idle("A");
        try
        {
            Check(ServerState.AdminControl.IsBanned("rimtest-B"), "Ban was not persisted.");
            await Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
            await entry.LoginExisting(false);
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "Unban", targetUserId = "rimtest-B" }); await Idle("A"); }
        Check(!ServerState.AdminControl.IsBanned("rimtest-B"), "Unban was not persisted.");
        await entry.LoginExisting(true);
    }
    async Task MaintenanceAndPassword()
    {
        await Invoke("B", "StartManualSnapshotUpload"); await Idle("B");
        await entry.ExitSession("B");
        await entry.LoginExisting(false, "incorrect-password");
        await Invoke("A", "StartAdminAction", new { actionKind = "LockMaintenance", message = "RimTest maintenance" }); await Idle("A");
        try
        {
            Check(ServerState.AdminControl.MaintenanceLoginLocked, "Maintenance lock not persisted.");
            await entry.LoginExisting(false);
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "UnlockMaintenance" }); await Idle("A"); }
        Check(!ServerState.AdminControl.MaintenanceLoginLocked, "Maintenance unlock not persisted.");
        await entry.LoginExisting(true);
    }
}
