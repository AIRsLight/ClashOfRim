using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using static RecoveryTestSupport;
using static EntryTestSupport;

internal static class EntryHandshakeModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var purpose in Enum.GetValues<EntryPurpose>()) Policy(purpose);
                FormatsAndLanguage(); Publication(); CurrentOptions();
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
    private static void Policy(EntryPurpose purpose)
    {
        var f = new Fixture(); var entry = f.Prepare(purpose);
        Require(entry.Authorize() is null && entry.UserId == "receiver" && entry.DisplayName == "receiver", "Independent entry policy rejected a valid credential.");
        Require(f.Baseline.Current is null && f.World.GetSessionState("receiver").AdministratorUserId is null
            && f.Sessions.FindSessionIdForColony("receiver", "colony") is null, "Authorization published a baseline, administrator or session.");
        f.Admin.SetMaintenanceLoginLocked(true, "maintenance-é");
        Require((entry.Authorize() is null) == (purpose == EntryPurpose.WorldPreparation), "Bootstrap maintenance exception leaked between entry purposes.");
        f.World.Prepare("admin");
        Require(entry.Authorize()?.Message == "maintenance-é", "Non-administrator ignored current maintenance state.");
        f.World.PromoteAdministrator("receiver"); Require(entry.Authorize() is null, "Administrator maintenance access was rejected.");
        f.World.RevokeAdministrator("receiver"); Require(entry.Authorize() is not null && entry.Compatibility is null, "Revoked role reused prior compatibility result.");
        f.Admin.SetMaintenanceLoginLocked(false, null);
        Require(f.Sessions.TryCreate("receiver", "colony", f.At, out var session)
            && f.Sessions.TryBeginStream("receiver", "colony", session, f.At), "Could not seed active session.");
        Require((entry.Authorize() is null) == (purpose == EntryPurpose.WorldPreparation), "Active session exclusion changed for world preparation/login.");
        f.Sessions.End("receiver", session);
        f.Admin.Ban("receiver"); Require(entry.Authorize() is not null && entry.Compatibility is null, "Banned user retained entry authorization.");
        f.Admin.Unban("receiver"); Require(entry.Authorize() is null, "Restored policy could not authorize the original proof.");
        f.Baseline.ReplaceBaseline(Manifest("incompatible", "different"), "admin", f.At);
        Require(entry.Authorize() is not null && entry.Compatibility?.CompatibilityIssues.Count > 0, "Entry used an obsolete compatibility baseline.");
        f.Baseline.ReplaceBaseline(Manifest("replacement"), "admin", f.At);
        Require(entry.Authorize() is null, "Compatible replacement was incorrectly rejected.");
        f.Accounts.ResetPassword("receiver", "pw", f.At, out _);
        Require(entry.Authorize() is not null && entry.Compatibility is null, "Same-password credential replacement retained an old proof.");
        entry = f.Prepare(purpose); Require(entry.Authorize() is null, "Fresh proof rejected after password replacement.");
        f.Configuration = new(authenticationDebugMode: true);
        Require(entry.Authorize() is not null && entry.Compatibility is null, "Provider configuration change retained old authorization.");
    }
    private static void FormatsAndLanguage()
    {
        var f = new Fixture(); var server = Manifest() with { GameLanguage = "English" };
        f.Baseline.EnsureBaseline(server, "admin", f.At);
        ColonyDeletionStageTests.SeedWorld(f.World, gameLanguage: "ChineseSimplified");
        foreach (string kind in new[] { "full", "summary", "id" })
        {
            var input = kind == "full" ? Input(server) : CompatibilityHandshakeInput.Prepare(null, kind == "id" ? " base " : null,
                kind == "summary" ? JsonSerializer.Serialize(new CompatibilityManifestSummary { ManifestId = "base", ProtocolVersion = ProtocolApiVersion.Current,
                    RimWorldVersion = "1.7", GameLanguage = "English" }) : null);
            var result = f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver", "display-é"), input, f.At, false);
            Require(result.Result.Accepted && result.AuthenticatedUserId == "receiver" && result.DisplayName == "display-é"
                && result.CompatibilityIssues.Single().Code == nameof(CompatibilityIssueCode.GameLanguageMismatch)
                && result.ServerCompatibilityManifestJson is not null && !result.RequiresFullCompatibilityManifest,
                "Handshake lost language warning, identity or manifest delivery: " + kind);
            Require(CompatibilityHandshakeContract.TryReadCompatibilityManifest(result.ServerCompatibilityManifestJson, out var roundtrip, out _)
                && roundtrip!.GameLanguage == "English", "Delivered manifest lost its wire contract.");
        }
        foreach (string invalid in new[] { "null", "{", "{}", "{\"mods\":null}", JsonSerializer.Serialize(server with { DlcIds = ["x", "X"] }) })
        {
            var result = f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), CompatibilityHandshakeInput.Prepare(invalid, "base", null), f.At, false);
            Require(!result.Result.Accepted && f.Baseline.Current == server, "Malformed full input bypassed validation through manifest ID.");
        }
        var need = f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), CompatibilityHandshakeInput.Prepare(null, "different", "{}"), f.At, false);
        Require(!need.Result.Accepted && need.RequiresFullCompatibilityManifest && need.RequestedCompatibilityPackageIds.SequenceEqual(new[] { "*" }), "Missing usable summary did not request the full manifest.");
        var denied = f.Handshake.Evaluate(AuthenticationValidationResult.Reject("denied"), Input(server), f.At, true);
        Require(!denied.Result.Accepted && denied.AuthenticatedUserId is null && !denied.CanOverrideCompatibilityBaseline, "Rejected credential gained compatibility privileges.");
    }
    private static void Publication()
    {
        var f = new Fixture(register: false); var entry = f.Prepare(EntryPurpose.WorldPreparation, create: true);
        Require(entry.Authorize() is null && f.AccountSlot.Read() is null && f.BaselineSlot.Read() is null, "Read-only entry persisted initial account or baseline.");
        f.AccountSlot.Fail = true;
        ExpectIo(() => entry.CommitAccount(f.At));
        Require(f.AccountSlot.Read() is null && f.Baseline.Current is null, "Failed credential commit published later state.");
        f.AccountSlot.Fail = false; Require(entry.CommitAccount(f.At) is null, "Credential commit could not retry.");
        f.BaselineSlot.Fail = true; ExpectIo(() => entry.PublishBaseline(f.At));
        Require(f.Baseline.Current is null && new CompatibilityBaselineRegistry(f.BaselineSlot).Current is null, "Failed baseline persistence escaped into memory/reopen.");
        f.BaselineSlot.Fail = false; Require(entry.PublishBaseline(f.At) is null, "Baseline publish could not retry.");
        Require(new OfflineAccountRegistry(f.AccountSlot).Authenticate("receiver", "pw", f.At).Accepted
            && new CompatibilityBaselineRegistry(f.BaselineSlot).Current!.ManifestId == "base"
            && f.World.GetSessionState("receiver").AdministratorUserId is null && f.Sessions.FindSessionIdForColony("receiver", "colony") is null,
            "Entry proof lost durable facts or published unrelated lifecycle state.");
    }
    private static void CurrentOptions()
    {
        var f = new Fixture();
        var server = Manifest() with { Mods = [new() { PackageId = "mod-é", Configs = [new() { FileName = "settings.xml", Sha256 = "server" }] }] };
        var client = server with { Mods = [server.Mods[0] with { Configs = [new() { FileName = "settings.xml", Sha256 = "client" }] }] };
        f.Baseline.EnsureBaseline(server, "admin", f.At);
        f.Options = new() { ModConfigRules = [new() { PackageId = "mod-é", Mode = ModConfigComparisonMode.Ignore }] };
        var input = Input(client);
        Require(f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), input, f.At, false).Result.Accepted, "Ignore configuration rule was not applied.");
        f.Options = new();
        Require(!f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), input, f.At, false).Result.Accepted, "Handshake cached obsolete comparison options.");
        f.Options = new() { ModConfigRules = [new() { PackageId = "new-rule", Mode = ModConfigComparisonMode.Ignore }] };
        Require(CompatibilityHandshakeContract.TryReadCompatibilityManifest(f.Handshake.Serialize(server), out var delivered, out _)
            && delivered!.ModConfigRules.Single().PackageId == "new-rule" && server.ModConfigRules.Count == 0, "Manifest enrichment mutated baseline or cached old rules.");
        var summary = new CompatibilityManifestSummary { ManifestId = "summary", ProtocolVersion = ProtocolApiVersion.Current,
            RimWorldVersion = "1.7", Mods = [new() { PackageId = "mod-é", Hash = "different" }] };
        var result = f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), CompatibilityHandshakeInput.Prepare(null, null, JsonSerializer.Serialize(summary)), f.At, false);
        Require(!result.Result.Accepted && result.RequiresFullCompatibilityManifest && result.RequestedCompatibilityPackageIds.SequenceEqual(new[] { "mod-é" }), "Changed summary did not request the exact package details.");
        var full = server with { Mods = [server.Mods[0], new() { PackageId = "second", LoadOrder = 1 }] };
        f.Baseline.ReplaceBaseline(full, "admin", f.At);
        Require(f.Handshake.Evaluate(AuthenticationValidationResult.Accept("receiver"), Input(server), f.At, false).Result.Accepted, "Existing selective-details exchange no longer accepts an unchanged requested package.");
    }
    private sealed class Fixture
    {
        internal readonly DateTimeOffset At = DateTimeOffset.UtcNow;
        internal readonly Slot AccountSlot = new(), BaselineSlot = new();
        internal readonly OfflineAccountRegistry Accounts;
        internal readonly CompatibilityBaselineRegistry Baseline;
        internal readonly WorldConfigurationRegistry World = new();
        internal readonly AdminControlRegistry Admin = new();
        internal readonly LoginSessionRegistry Sessions = new();
        internal readonly AccountService Credentials;
        internal readonly CompatibilityHandshakeService Handshake;
        internal readonly EntryHandshakePolicy Policy;
        internal ClashOfRimServerConfiguration Configuration = new();
        internal CompatibilityComparisonOptions Options = new();
        internal Fixture(bool register = true)
        {
            Accounts = new(AccountSlot); Baseline = new(BaselineSlot);
            if (register) Require(Accounts.Authenticate("receiver", "pw", At, true).Accepted, "Cannot seed offline credential.");
            Credentials = new(Accounts, new(), new SteamValidator(), () => Configuration);
            Handshake = new(Baseline, World, () => Options); Policy = new(Admin, World, Sessions, Handshake);
        }
        internal EntryHandshakePolicy.PreparedEntryHandshake Prepare(EntryPurpose purpose, bool create = false)
            => Policy.Prepare(Credentials.PrepareCredentials("receiver", null, "pw", At, create), Input(Manifest()), purpose);
    }
    private static CompatibilityHandshakeInput Input(CompatibilityManifest manifest) => CompatibilityHandshakeInput.Prepare(JsonSerializer.Serialize(manifest), null, null);
    private static void ExpectIo(Action action)
    { try { action(); throw new InvalidOperationException("Expected persistence failure."); } catch (IOException) { } }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
