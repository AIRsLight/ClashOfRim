using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameAdministrationScenarioTests
{
    internal static void Permissions() => PermissionsAsync().GetAwaiter().GetResult();
    internal static void Sessions() => SessionsAsync().GetAwaiter().GetResult();
    internal static void Passwords() => PasswordsAsync().GetAwaiter().GetResult();
    internal static void Cleanup() => CleanupAsync().GetAwaiter().GetResult();

    private static async Task PermissionsAsync()
    {
        using (var f = await Fixture.Create())
        {
            await f.Admin.Configuration();
            Check(JsonNode.DeepEquals(f.Configuration, f.Original), "Configuration lost fields on restoration.");
            Check(f.Actions("StartUpdateAdminConfiguration").Select(c => c.Client).SequenceEqual(new[] { "A", "B", "A" }), "Configuration skipped owner/guest/restore actions.");
            Check(f.Actions("StartRefreshAdminStatus").Count() == 4, "Configuration omitted a refreshed observation.");
        }
        using (var f = await Fixture.Create())
        {
            await f.Admin.Guards();
            Check(f.AdminActions.Select(a => (a.Client, a.Kind)).SequenceEqual(new[] {
                ("B", "LockMaintenance"), ("B", "PromoteAdmin"), ("B", "Ban"), ("B", "Kick"), ("B", "DeletePlayerSave"), ("B", "ResetOfflinePassword"),
                ("A", "Kick"), ("A", "Ban"), ("A", "RevokeAdmin"), ("A", "DeletePlayerSave") }), "Admin rejection matrix changed.");
            Check(f.AdminActions.All(a => a.Target == "rimtest-A") && f.Control.ListAudit().Count == 0, "Admin guards used another target or inserted audit records.");
        }
        using (var f = await Fixture.Create())
        {
            await f.Admin.Roles();
            Check(f.AdminActions.Select(a => a.Kind).SequenceEqual(new[] { "PromoteAdmin", "RevokeAdmin" }) && !f.World.IsAdministrator("rimtest-B"), "Role test did not restore the guest role.");
        }
        using (var f = await Fixture.Create())
        {
            await f.Admin.Broadcast();
            var action = f.Actions("StartAdminAction").Single().Args.GetProperty("arguments");
            Check(action.String("targetUserId") == "rimtest-B" && action.Bool("persistentNotification"), "Broadcast changed its target or persistence.");
            Check(f.Actions("StartRefreshEventsForTest").Single().Client == "B", "Broadcast bypassed guest event refresh.");
        }
        foreach (var (fault, scenario) in new (string, Func<Fixture, Task>)[]
        {
            ("status-rejected", f => f.Admin.Configuration()), ("config-refused", f => f.Admin.Configuration()),
            ("unauthorized-config", f => f.Admin.Configuration()), ("restore-fields", f => f.Admin.Configuration()),
            ("unauthorized-audit", f => f.Admin.Guards()), ("unauthorized-maintenance", f => f.Admin.Guards()),
            ("unauthorized-ban", f => f.Admin.Guards()), ("unauthorized-kick", f => f.Admin.Guards()),
            ("self-audit", f => f.Admin.Guards()), ("self-role", f => f.Admin.Guards()),
            ("role-store", f => f.Admin.Roles()), ("revoke-store", f => f.Admin.Roles()), ("broadcast-audit", f => f.Admin.Broadcast())
        })
        {
            using var f = await Fixture.Create(fault);
            await Reject(() => scenario(f), fault);
        }
    }
    private static async Task SessionsAsync()
    {
        using (var f = await Fixture.Create())
        {
            await f.Admin.Kick();
            Check(f.Logins.SequenceEqual(new[] { true }) && f.Active.Values.All(v => v) && f.Runtime.Count("time.step") == 1, "Kick did not reconnect and stabilize the guest.");
        }
        using (var f = await Fixture.Create())
        {
            await f.Admin.Ban();
            Check(f.Logins.SequenceEqual(new[] { false, true }) && !f.Control.IsBanned("rimtest-B") && f.Active["A"], "Ban skipped refused login or recovery after unban.");
        }
        using (var f = await Fixture.Create())
        {
            await f.Admin.MaintenanceAndPassword();
            Check(f.Logins.SequenceEqual(new[] { false, false, true }) && f.Runtime.Count("game.returnToMenu") == 1 && f.Actions("StartManualSnapshotUpload").Count() == 1,
                "Maintenance skipped snapshot logout, wrong password, lock or final login.");
            Check(!f.Control.MaintenanceLoginLocked && f.Runtime.Count("time.step") == 1, "Maintenance did not restore access or stabilized a rejected login.");
            var calls = f.Runtime.Calls;
            Check(calls.FindIndex(c => c.Method == "game.returnToMenu") > calls.FindIndex(c => c.Method == "clash.invoke" && c.Args.String("action") == "StartManualSnapshotUpload"), "Maintenance exited before uploading.");
        }
        foreach (var (fault, scenario) in new (string, Func<Fixture, Task>)[] {
            ("kick-owner", f => f.Admin.Kick()), ("login-refused", f => f.Admin.Kick()),
            ("ban-store", f => f.Admin.Ban()), ("unban-store", f => f.Admin.Ban()), ("lock-store", f => f.Admin.MaintenanceAndPassword()) })
        {
            using var f = await Fixture.Create(fault); await Reject(() => scenario(f), fault);
        }
        using (var f = await Fixture.Create())
        {
            f.ThrowAfter = "StartManualSnapshotUpload";
            await Interrupted(f.Admin.MaintenanceAndPassword);
            Check(f.Runtime.Count("game.returnToMenu") == 0 && f.AdminActions.Count == 0, "Failed upload still discarded the game or locked maintenance.");
        }
    }
    private static async Task PasswordsAsync()
    {
        using (var f = await Fixture.Create())
        {
            await f.Password.Change();
            var changes = f.Actions("StartChangeOfflinePassword").ToArray();
            Check(changes.Length == 3 && changes.All(c => c.Client == "B") && f.HasInitialPassword(), "Password test did not reject, change and restore on the guest account.");
            Check(changes.Select(c => c.Args.GetProperty("arguments").String("currentPassword")).SequenceEqual(new[] {
                "incorrect-password", "local-test-password", "rimtest-replacement-password" }), "Password test changed authentication order.");
            Check(f.Accounts.Authenticate("rimtest-A", "local-test-password", DateTimeOffset.UtcNow).Accepted, "Guest password test changed the owner credentials.");
        }
        foreach (string fault in new[] { "password-wrong-accepted", "password-refused", "password-restore-refused" })
        {
            using var f = await Fixture.Create(fault);
            await Reject(f.Password.Change, fault);
            if (fault != "password-restore-refused") Check(f.HasInitialPassword(), "Failed password assertion left replacement credentials active.");
        }
    }
    private static async Task CleanupAsync()
    {
        foreach (var (start, restore, scenario) in new (string, string, Func<Fixture, Task>)[]
        {
            ("StartUpdateAdminConfiguration", "StartUpdateAdminConfiguration", f => f.Admin.Configuration()),
            ("PromoteAdmin", "RevokeAdmin", f => f.Admin.Roles()), ("Ban", "Unban", f => f.Admin.Ban()),
            ("LockMaintenance", "UnlockMaintenance", f => f.Admin.MaintenanceAndPassword()),
            ("password-change", "password-restore", f => f.Password.Change())
        })
        {
            using var f = await Fixture.Create(); f.ThrowAfter = start;
            await Interrupted(() => scenario(f));
            Check(JsonNode.DeepEquals(f.Configuration, f.Original) && !f.World.IsAdministrator("rimtest-B") && !f.Control.IsBanned("rimtest-B")
                && !f.Control.MaintenanceLoginLocked && f.HasInitialPassword(), "Interrupted operation leaked temporary state: " + start);
            Check(f.Operations.Last(o => o != "StartRefreshAdminStatus") == restore && f.Operations.Count(o => o == start) == (start == restore ? 2 : 1), "Cleanup retried the failed operation or skipped its counterpart: " + start);
        }
        foreach (var (phase, scenario) in new (string, Func<Fixture, Task>)[]
        {
            ("config-observation", f => f.Admin.Configuration()), ("role-observation", f => f.Admin.Roles()),
            ("login-observation", f => f.Admin.Ban()), ("maintenance-observation", f => f.Admin.MaintenanceAndPassword()),
            ("password-observation", f => f.Password.Change())
        })
        {
            using var f = await Fixture.Create(); f.ThrowObservation = phase;
            await Interrupted(() => scenario(f));
            Check(JsonNode.DeepEquals(f.Configuration, f.Original) && !f.World.IsAdministrator("rimtest-B") && !f.Control.IsBanned("rimtest-B")
                && !f.Control.MaintenanceLoginLocked && f.HasInitialPassword(), "Failed observation leaked temporary state: " + phase);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly AdministrationScenario Admin;
        internal readonly PasswordScenario Password;
        internal readonly AdminControlRegistry Control = new();
        internal readonly WorldConfigurationRegistry World = new();
        internal readonly OfflineAccountRegistry Accounts = new();
        internal readonly Dictionary<string, bool> Active = new() { ["A"] = true, ["B"] = true };
        internal readonly List<(string Client, string Kind, string? Target)> AdminActions = [];
        internal readonly List<bool> Logins = [];
        internal readonly List<string> Operations = [];
        internal readonly JsonNode Original = JsonNode.Parse("{\"giftsEnabled\":true,\"fraction\":0.25,\"unknown\":{\"name\":\"中文 é\"}}")!;
        internal JsonNode Configuration;
        internal string? ThrowAfter, ThrowObservation;
        private readonly string fault;
        private bool thrown, role, broadcast;
        private int epoch, passwordChanges;
        private readonly Dictionary<string, int> busy = [];
        private Fixture(GameScenarioTestRuntime runtime, string fault)
        {
            Runtime = runtime; this.fault = fault; Configuration = Original.DeepClone();
            World.Prepare("rimtest-A");
            foreach (string user in new[] { "rimtest-A", "rimtest-B" }) Check(Accounts.Authenticate(user, "local-test-password", DateTimeOffset.UtcNow, true).Accepted, "Account setup failed.");
            Admin = new(runtime.Run, runtime.Polling, runtime.CreateEntry(isUserOnline: user => Active[user[^1..]]), Control, World);
            Password = new(runtime.Run, runtime.Polling, Accounts);
            runtime.Script = Reply;
        }
        internal static async Task<Fixture> Create(string fault = "none") => new(await GameScenarioTestRuntime.Create(), fault);
        internal IEnumerable<(string Client, string Method, JsonElement Args)> Actions(string action) => Runtime.Calls.Where(c => c.Method == "clash.invoke" && c.Args.String("action") == action);
        internal bool HasInitialPassword() => Accounts.Authenticate("rimtest-B", "local-test-password", DateTimeOffset.UtcNow).Accepted;
        private object? Reply(string client, string method, JsonElement args)
        {
            switch (method)
            {
                case "clash.state":
                    if (client == "B" && role) FailObservation("role-observation");
                    if (client == "B" && Control.MaintenanceLoginLocked) FailObservation("maintenance-observation");
                    if (client == "B" && passwordChanges == 2) FailObservation("password-observation");
                    bool waiting = busy.GetValueOrDefault(client) > 0; busy[client] = 0;
                    return new { isAdministrator = client == "A" || role, isInActiveMultiplayerSession = Active[client], adminInProgress = waiting, accountPasswordInProgress = waiting };
                case "clash.admin":
                    if (!Configuration["giftsEnabled"]!.GetValue<bool>()) FailObservation("config-observation");
                    return new { isAdministrator = client == "A" || role, result = new { accepted = fault != "status-rejected" }, configuration = Configuration.DeepClone() };
                case "clash.events":
                    Check(broadcast && client == "B", "Broadcast did not reach the recipient observation.");
                    return new { items = new[] { new { data = new { eventType = "chat", payloadSummary = JsonSerializer.Serialize(new { Message = "RimTest notice 中文 Français 1,25" }) } },
                        new { data = new { eventType = "serverNotification", payloadSummary = JsonSerializer.Serialize(new { Message = "RimTest notice 中文 Français 1,25" }) } } } };
                case "game.state": return new { epoch = epoch.ToString(), playing = Active[client], playerHasControl = Active[client], mapCount = Active[client] ? 1 : 0, startupReady = !Active[client], hasGame = Active[client] };
                case "clash.inventory": return new { maps = new[] { new { items = new Dictionary<string, int>(), colonists = Array.Empty<object>() } } };
                case "game.returnToMenu": Check(client == "B" && args.Bool("discardUnsavedChanges"), "Maintenance exited the wrong game."); Active[client] = false; return new { };
                case "clash.invoke": return Invoke(client, args);
                default: return null;
            }
        }
        private object Invoke(string client, JsonElement args)
        {
            string action = args.String("action"); var input = args.GetProperty("arguments"); string operation = action;
            switch (action)
            {
                case "StartRefreshAdminStatus": case "StartRefreshEventsForTest": break;
                case "StartManualSnapshotUpload": Check(client == "B", "Maintenance uploaded the wrong client."); break;
                case "StartUpdateAdminConfiguration":
                    if (client == "A" && fault != "config-refused" || client == "B" && fault == "unauthorized-config") Configuration = JsonNode.Parse(input.GetProperty("configuration").GetRawText())!;
                    if (client == "A" && Configuration["giftsEnabled"]!.GetValue<bool>() && fault == "restore-fields") Configuration["fraction"] = 999;
                    busy[client] = 1; break;
                case "StartAdminAction":
                    operation = input.String("actionKind"); ApplyAdmin(client, operation, input); busy[client] = 1; break;
                case "StartMainMenuServerEntryFlow":
                    Check(client == "B" && input.String("serverBaseUrl") == "http://test-server" && input.String("userId") == "rimtest-B" && !input.Bool("createAccountIfMissing"), "Admin scenario bypassed real login routing.");
                    FailObservation("login-observation");
                    if (fault == "login-refused") return new { returnValue = false };
                    Active[client] = !Control.IsBanned("rimtest-B") && !Control.MaintenanceLoginLocked && Accounts.Authenticate("rimtest-B", input.String("password"), DateTimeOffset.UtcNow).Accepted;
                    Logins.Add(Active[client]); if (Active[client]) epoch++; break;
                case "StartChangeOfflinePassword":
                    Check(client == "B", "Password scenario changed another client.");
                    passwordChanges++; operation = passwordChanges switch { 1 => "password-wrong", 2 => "password-change", _ => "password-restore" };
                    if (fault == "password-wrong-accepted" && passwordChanges == 1) Accounts.ResetPassword("rimtest-B", input.String("newPassword"), DateTimeOffset.UtcNow, out _);
                    else if (!(fault == "password-refused" && passwordChanges == 2 || fault == "password-restore-refused" && passwordChanges == 3))
                        Accounts.ChangePassword("rimtest-B", input.String("currentPassword"), input.String("newPassword"), DateTimeOffset.UtcNow, out _);
                    busy[client] = 1; break;
                default: throw new InvalidOperationException("Unexpected admin/password action: " + action);
            }
            Operations.Add(operation);
            if (!thrown && operation == ThrowAfter) { thrown = true; throw new IOException("Interrupted response: " + operation); }
            return new { returnValue = true };
        }
        private void ApplyAdmin(string client, string kind, JsonElement input)
        {
            string? target = input.OptionalString("targetUserId"); AdminActions.Add((client, kind, target));
            if (client == "B")
            {
                if (fault == "unauthorized-audit") Control.AddAudit(kind, "rimtest-B", target, null, DateTimeOffset.UtcNow);
                if (fault == "unauthorized-maintenance") Control.SetMaintenanceLoginLocked(true, "wrong");
                if (fault == "unauthorized-ban") Control.Ban("rimtest-A");
                if (fault == "unauthorized-kick") Active["A"] = false;
                return;
            }
            if (target == "rimtest-A")
            {
                if (fault == "self-audit") Control.AddAudit(kind, "rimtest-A", target, null, DateTimeOffset.UtcNow);
                if (fault == "self-role") { World.PromoteAdministrator("rimtest-B"); World.RevokeAdministrator("rimtest-A"); }
                return;
            }
            switch (kind)
            {
                case "PromoteAdmin": role = true; if (fault != "role-store") World.PromoteAdministrator(target!); break;
                case "RevokeAdmin": role = false; if (fault != "revoke-store") World.RevokeAdministrator(target!); break;
                case "Ban": if (fault != "ban-store") Control.Ban(target!); Active["B"] = false; break;
                case "Unban": if (fault != "unban-store") Control.Unban(target!); break;
                case "Kick": Active["B"] = false; if (fault == "kick-owner") Active["A"] = false; break;
                case "LockMaintenance": if (fault != "lock-store") Control.SetMaintenanceLoginLocked(true, input.OptionalString("message")); break;
                case "UnlockMaintenance": Control.SetMaintenanceLoginLocked(false, null); break;
                case "Broadcast": broadcast = true; if (fault != "broadcast-audit") Control.AddAudit(kind, "rimtest-A", target, input.String("message"), DateTimeOffset.UtcNow); break;
                default: throw new InvalidOperationException("Unexpected admin kind: " + kind);
            }
        }
        private void FailObservation(string phase)
        { if (!thrown && ThrowObservation == phase) { thrown = true; throw new IOException("Interrupted observation: " + phase); } }
        public void Dispose() => Runtime.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string context)
    { try { await action(); } catch (AssertionException) { return; } throw new InvalidOperationException("Expected admin/password assertion: " + context); }
    private static async Task Interrupted(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected injected interruption."); }
}
