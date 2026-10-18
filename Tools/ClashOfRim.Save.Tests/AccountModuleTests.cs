using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;
using static RecoveryTestSupport;

internal static class AccountModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        // A rejected/failed first password change must not register a blank account in memory.
        foreach (string failure in new[] { "wrong-password", "persistence" })
        {
            var f = new Fixture(register: false); f.Slot.Fail = failure == "persistence";
            try
            {
                var response = f.Accounts.ChangePassword(f.Request(failure == "wrong-password" ? "wrong" : "", "new"), f.At);
                Check(!response.Result.Accepted, "Expected initial password change to be rejected.");
            }
            catch (IOException) when (failure == "persistence") { }
            Check(f.Registry.Authenticate("user-é", "", f.At).Message == OfflineAccountRegistry.MissingUserKey && f.Slot.Read() is null,
                "Failed password change published a transient empty-password account: " + failure);
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                ChangeAndReopen(); Authorization(); FailedPersistence();
            }
            Competition(); PreparedCredentials(); DurableStores();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void ChangeAndReopen()
    {
        var f = new Fixture(); string before = f.Slot.Read()!;
        var response = f.Accounts.ChangePassword(f.Request("old-文化", "new-İ-é"), f.At);
        Check(response.Result.Accepted && !string.IsNullOrWhiteSpace(response.Result.Message) && f.Slot.Read() != before, "Password change was not accepted/durable.");
        Check(f.Registry.Authenticate("user-é", "new-İ-é", f.At).Accepted && !f.Registry.Authenticate("user-é", "old-文化", f.At).Accepted,
            "New and old password authentication did not switch.");
        var reopened = new OfflineAccountRegistry(f.Slot);
        Check(reopened.Authenticate("user-é", "new-İ-é", f.At).Accepted && !reopened.Authenticate("user-é", "old-文化", f.At).Accepted, "Reopened account has another password.");
        int writes = f.Slot.Writes;
        Check(!f.Accounts.ChangePassword(f.Request("old-文化", "new-İ-é"), f.At).Result.Accepted && f.Slot.Writes == writes,
            "A duplicate non-idempotent password command rewrote the account.");
        Check(f.Tokens.IsValid(f.Token, "user-é", "colony", f.At), "Changing credentials unexpectedly revoked the authenticated session.");
        Check(f.Accounts.ChangePassword(f.Request("new-İ-é", null), f.At).Result.Accepted
            && new OfflineAccountRegistry(f.Slot).Authenticate("user-é", null, f.At).Accepted, "Empty replacement password changed its legacy meaning.");
        var newAccount = new Fixture(register: false);
        Check(newAccount.Accounts.ChangePassword(newAccount.Request(null, "new"), newAccount.At).Result.Accepted
            && new OfflineAccountRegistry(newAccount.Slot).Authenticate("user-é", "new", newAccount.At).Accepted,
            "A valid authenticated account could not establish its first offline credential.");
    }

    private static void Authorization()
    {
        foreach (string change in new[] { "missing", "unknown", "user", "colony", "case", "revoked", "expired" })
        {
            var f = new Fixture(); string before = f.Slot.Read()!;
            var request = new ChangeOfflinePasswordRequest(change == "user" ? "another" : change == "case" ? "USER-é" : "user-é",
                change == "colony" ? "another" : "colony", change == "missing" ? null : change == "unknown" ? "unknown" : f.Token,
                "old-文化", "new");
            if (change == "revoked") f.Tokens.Revoke(f.Token);
            var response = f.Accounts.ChangePassword(request, change == "expired" ? f.At.AddHours(12) : f.At);
            Check(!response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.Unauthorized && f.Slot.Read() == before,
                "Unauthorized password change affected persisted accounts: " + change);
            Check(f.Registry.Authenticate("user-é", "old-文化", f.At).Accepted, "Authorization rejection changed credentials.");
        }
        var boundary = new Fixture();
        Check(boundary.Accounts.ChangePassword(boundary.Request("old-文化", "new"), boundary.At.AddHours(12).AddTicks(-1)).Result.Accepted,
            "Credential change rejected a token before its expiry boundary.");
    }

    private static void FailedPersistence()
    {
        var f = new Fixture(); string before = f.Slot.Read()!;
        var proof = f.Accounts.PrepareCredentials("user-é", null, "old-文化", f.At);
        f.Slot.Fail = true; bool failed = false;
        try { f.Accounts.ChangePassword(f.Request("old-文化", "new"), f.At); } catch (IOException) { failed = true; }
        Check(failed && f.Slot.Read() == before && proof.Validate(f.At, false).Accepted && f.Registry.Authenticate("user-é", "old-文化", f.At).Accepted,
            "Failed persistence published replacement credentials or invalidated the original proof.");
        f.Slot.Fail = false;
        Check(f.Accounts.ChangePassword(f.Request("old-文化", "new"), f.At).Result.Accepted && !proof.Validate(f.At, false).Accepted,
            "Retried password persistence failed or retained a replaced proof.");
    }

    private static void Competition()
    {
        var f = new Fixture();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => (Password: "new-" + i,
            Response: f.Accounts.ChangePassword(f.Request("old-文化", "new-" + i), f.At)))).ToArray();
        Task.WaitAll(tasks);
        var winner = tasks.Select(t => t.Result).Single(r => r.Response.Result.Accepted);
        Check(new OfflineAccountRegistry(f.Slot).Authenticate("user-é", winner.Password, f.At).Accepted && f.Slot.Writes == 2,
            "Competing password changes persisted more than one replacement.");
    }

    private static void PreparedCredentials()
    {
        var f = new Fixture(register: false);
        var proof = f.Accounts.PrepareCredentials("user-é", null, "old", f.At, createAccount: true);
        Check(proof.Result.Accepted && f.Slot.Read() is null, "Credential preparation created an account.");
        Check(proof.Commit(f.At).Accepted && f.Accounts.Authenticate("user-é", null, "old", f.At).Accepted, "Account module changed prepare/commit semantics.");
        var stale = f.Accounts.PrepareCredentials("user-é", null, "old", f.At);
        f.Configuration = new(authenticationDebugMode: true);
        Check(!stale.Validate(f.At, false).Accepted, "Prepared credentials survived authentication provider replacement.");
        f.Steam.Identity = "user-é";
        Check(f.Accounts.Authenticate("user-é", "ticket", null, f.At).Accepted && f.Steam.Calls == 1, "Account module bypassed the existing Steam validator.");
    }

    private static void DurableStores()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashOfRim-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool sqlite in new[] { false, true })
            {
                string path = Path.Combine(root, sqlite ? "accounts.db" : "accounts.json");
                OfflineAccountRegistry Open() => sqlite
                    ? new(new SqliteDomainKeyedJsonRecordStore(path, SqliteDomainRegistrySchema.OfflineAccounts), null)
                    : new(new FileJsonPersistenceSlot(path));
                var registry = Open(); var at = DateTimeOffset.UtcNow;
                Check(registry.Authenticate("user", "old", at, true).Accepted, "Could not create durable account fixture.");
                var tokens = new AuthTokenRegistry(); string token = tokens.Issue("user", "user", "colony", "session", at);
                var service = new AccountService(registry, tokens, new SteamValidator(), () => new());
                var request = new ChangeOfflinePasswordRequest("user", "colony", token, "old", "new");
                using var connection = sqlite ? new SqliteConnection("Data Source=" + path + ";Pooling=False") : null;
                if (sqlite) { connection!.Open(); Execute("CREATE TRIGGER account_fault BEFORE INSERT ON server_offline_accounts BEGIN SELECT RAISE(ABORT, 'injected'); END;"); }
                using var locked = !sqlite && OperatingSystem.IsWindows() ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                if (sqlite || locked is not null)
                {
                    bool failed = false;
                    try { service.ChangePassword(request, at); }
                    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { failed = true; }
                    Check(failed && registry.Authenticate("user", "old", at).Accepted && Open().Authenticate("user", "old", at).Accepted,
                        "Durable write failure changed live or reopened credentials: " + sqlite);
                }
                locked?.Dispose(); if (sqlite) Execute("DROP TRIGGER account_fault;");
                Check(service.ChangePassword(request, at).Result.Accepted && Open().Authenticate("user", "new", at).Accepted,
                    "Durable password retry did not survive reopening: " + sqlite);
                void Execute(string sql) { using var command = connection!.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private sealed class Fixture
    {
        internal readonly DateTimeOffset At = DateTimeOffset.UtcNow;
        internal readonly Slot Slot = new();
        internal readonly AuthTokenRegistry Tokens = new();
        internal readonly SteamValidator Steam = new();
        internal readonly OfflineAccountRegistry Registry;
        internal readonly AccountService Accounts;
        internal readonly string Token;
        internal ClashOfRimServerConfiguration Configuration = new();
        internal Fixture(bool register = true)
        {
            Registry = new(Slot);
            if (register) Check(Registry.Authenticate("user-é", "old-文化", At, true).Accepted, "Could not register fixture.");
            Token = Tokens.Issue("user-é", "user-é", "colony", "session", At);
            Accounts = new(Registry, Tokens, Steam, () => Configuration);
        }
        internal ChangeOfflinePasswordRequest Request(string? current, string? next) => new("user-é", "colony", Token, current, next);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
