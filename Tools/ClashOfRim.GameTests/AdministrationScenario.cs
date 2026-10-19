using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class AdministrationScenario(TestRun run, ScenarioPolling polling, ServerEntryScenario entry,
    AdminControlRegistry control, WorldConfigurationRegistry world)
{
    internal async Task Configuration()
    {
        var status = await Refresh("A");
        Check(status.Bool("isAdministrator") && status.GetProperty("result").Bool("accepted"), "Initial owner is not administrator.");
        var original = status.GetProperty("configuration");
        var changed = JsonNode.Parse(original.GetRawText())!;
        changed["giftsEnabled"] = false;
        try
        {
            await Update("A", changed);
            Check(!(await Refresh("A")).GetProperty("configuration").Bool("giftsEnabled"), "Administrator configuration did not persist.");
            await Update("B", original);
            Check(!(await Refresh("A")).GetProperty("configuration").Bool("giftsEnabled"), "Non-administrator changed configuration.");
        }
        finally
        {
            await Update("A", original);
            Equal(original, (await Refresh("A")).GetProperty("configuration"), "Configuration restore failed");
        }
    }
    internal async Task Guards()
    {
        int audits = control.ListAudit().Count;
        foreach (string kind in new[] { "LockMaintenance", "PromoteAdmin", "Ban", "Kick", "DeletePlayerSave", "ResetOfflinePassword" })
        {
            await Act("B", new { actionKind = kind, targetUserId = "rimtest-A", message = "unauthorized" });
        }
        Check(control.ListAudit().Count == audits && !control.MaintenanceLoginLocked && !control.IsBanned("rimtest-A"), "Unauthorized admin action changed server state.");
        Check((await run.CallAsync("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Unauthorized admin action kicked owner.");
        foreach (string kind in new[] { "Kick", "Ban", "RevokeAdmin", "DeletePlayerSave" })
        {
            await Act("A", new { actionKind = kind, targetUserId = "rimtest-A" });
        }
        Check(control.ListAudit().Count == audits && world.IsAdministrator("rimtest-A"), "Self-target protection failed.");
    }
    internal async Task Roles()
    {
        try
        {
            await Act("A", new { actionKind = "PromoteAdmin", targetUserId = "rimtest-B" });
            await polling.Wait("B", "clash.state", null, s => s.Bool("isAdministrator"));
            await run.InvokeClientAsync("B", "StartRefreshAdminStatus"); await polling.Idle("B");
            await polling.Wait("B", "clash.admin", null, s => s.ValueKind == JsonValueKind.Object && s.Bool("isAdministrator"));
            Check(world.IsAdministrator("rimtest-B"), "Promotion was not authoritative.");
        }
        finally { await Act("A", new { actionKind = "RevokeAdmin", targetUserId = "rimtest-B" }); }
        await polling.Wait("B", "clash.state", null, s => !s.Bool("isAdministrator"));
        Check(!world.IsAdministrator("rimtest-B"), "Revocation was not authoritative.");
    }
    internal async Task Broadcast()
    {
        const string message = "RimTest notice 中文 Français 1,25";
        await Act("A", new { actionKind = "Broadcast", targetUserId = "rimtest-B", message, persistentNotification = true });
        await run.InvokeClientAsync("B", "StartRefreshEventsForTest");
        await polling.Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(v => NotificationScenario.HasMessage(v, message)));
        Check(control.ListAudit().Any(a => a.ActionKind == "Broadcast" && a.Message == message), "Broadcast audit missing.");
    }
    internal async Task Kick()
    {
        Check((await run.CallAsync("B", "clash.state")).Bool("isInActiveMultiplayerSession"), "Guest must be online before kick.");
        await Act("A", new { actionKind = "Kick", targetUserId = "rimtest-B" });
        await polling.Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
        await entry.LoginExisting(true);
        Check((await run.CallAsync("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Kicking guest affected owner session.");
    }
    internal async Task Ban()
    {
        try
        {
            await Act("A", new { actionKind = "Ban", targetUserId = "rimtest-B" });
            Check(control.IsBanned("rimtest-B"), "Ban was not persisted.");
            await polling.Wait("B", "clash.state", null, s => !s.Bool("isInActiveMultiplayerSession"));
            await entry.LoginExisting(false);
        }
        finally { await Act("A", new { actionKind = "Unban", targetUserId = "rimtest-B" }); }
        Check(!control.IsBanned("rimtest-B"), "Unban was not persisted.");
        await entry.LoginExisting(true);
    }
    internal async Task MaintenanceAndPassword()
    {
        await entry.LogoutWithSnapshot("B");
        await entry.LoginExisting(false, "incorrect-password");
        try
        {
            await Act("A", new { actionKind = "LockMaintenance", message = "RimTest maintenance" });
            Check(control.MaintenanceLoginLocked, "Maintenance lock not persisted.");
            await entry.LoginExisting(false);
        }
        finally { await Act("A", new { actionKind = "UnlockMaintenance" }); }
        Check(!control.MaintenanceLoginLocked, "Maintenance unlock not persisted.");
        await entry.LoginExisting(true);
    }
    private async Task<JsonElement> Refresh(string client)
    {
        await run.InvokeClientAsync(client, "StartRefreshAdminStatus"); await polling.Idle(client);
        return await run.CallAsync(client, "clash.admin");
    }
    private async Task Update(string client, object configuration)
    {
        await run.InvokeClientAsync(client, "StartUpdateAdminConfiguration", new { configuration }); await polling.Idle(client);
    }
    private async Task Act(string client, object request)
    {
        await run.InvokeClientAsync(client, "StartAdminAction", request); await polling.Idle(client);
    }
}
