using System.Globalization;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LoginModuleTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "protocol", "identity", "password", "manifest", "details", "active", "ban" }) Invalid(mode);
                foreach (string mode in new[] { "success", "recovery-failure", "recovery-ban", "baseline-failure", "player-failure",
                    "logging-failure", "final-ban", "final-password", "final-defense", "final-replace" }) Publication(mode);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Invalid(string mode)
    {
        var f = new Fixture(); var old = f.S.Issue();
        if (mode == "active") Check(f.S.Sessions.TryBeginStream("user", "colony", old.Session, DateTimeOffset.UtcNow), "Cannot seed active connection.");
        if (mode == "ban") f.Admin.Ban("user");
        if (mode == "details") f.Baseline.EnsureBaseline(EntryTestSupport.Manifest(), "admin", DateTimeOffset.UtcNow);
        var request = new LoginRequest(mode == "protocol" ? "invalid" : ProtocolApiVersion.Current, mode == "identity" ? "" : "user", "colony", "reported", "",
            password: mode == "password" ? "incorrect" : "pw", compatibilityManifestJson: mode == "manifest" ? "{" : mode == "details" ? null : f.Manifest);
        var response = f.Login.Login(request); Denied(response);
        Check(f.S.Valid(old) && f.S.Recovery.Replays == 0 && f.S.PlayerSlot.Writes == 0
            && LoginPreparationTests.TokenCount(f.S.Tokens) == 1, "Rejected login recovered state, published activity or changed session ownership: " + mode);
        if (mode == "details") Check(response.RequiresFullCompatibilityManifest && response.RequestedCompatibilityPackageIds.SequenceEqual(new[] { "*" })
            && response.ServerCompatibilityManifestJson is not null, "Login discarded the manifest retry contract.");
    }

    private static void Publication(string mode)
    {
        var f = new Fixture(); var old = f.S.Issue();
        (string Session, string Token) replacement = default;
        f.S.Loans.Create("pending", "user", "colony", "base", 100, 1, 7, 1000, DateTimeOffset.UtcNow);
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate) && f.S.Valid(old) && f.Baseline.Current is null && f.S.PlayerSlot.Writes == 0,
                "Login published before detached recovery or prepared under the gate.");
            if (mode == "recovery-failure") throw new IOException("Recovery unavailable.");
            if (mode == "recovery-ban") f.Admin.Ban("user");
        };
        f.BaselineSlot.Fail = mode == "baseline-failure";
        f.S.PlayerSlot.Fail = mode == "player-failure";
        f.BeforeLog = () =>
        {
            Check(f.S.Valid(old) && LoginPreparationTests.TokenCount(f.S.Tokens) == 1
                && f.S.Loans.FindByIdempotencyKey("pending") is null, "Login published before lifecycle/response preparation completed.");
            if (mode == "logging-failure") throw new IOException("Logger unavailable.");
            if (mode == "final-ban") f.Admin.Ban("user");
            if (mode == "final-password") f.Accounts.ResetPassword("user", "pw", DateTimeOffset.UtcNow, out _);
            if (mode == "final-replace") replacement = f.S.Issue();
            if (mode == "final-defense")
            {
                var now = DateTimeOffset.UtcNow;
                f.S.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Raid, new("attacker", "other"), new("user", "colony"),
                    "defense", false, new RaidEventPayload("base", null, now, null, null, new("attacker-base", ["pawn"], [])), now));
            }
        };
        LoginResponse? result = null; bool threw = false;
        try { result = f.Login.Login(f.Request()); } catch (IOException) { threw = true; }
        bool failure = mode is "recovery-failure" or "baseline-failure" or "player-failure" or "logging-failure";
        Check(threw == failure, "Login swallowed or introduced a persistence/logging exception: " + mode);
        if (mode == "success") Accepted(f, result!, old.Session);
        else
        {
            if (!failure) Denied(result!);
            Check(LoginPreparationTests.TokenCount(f.S.Tokens) == (mode == "final-replace" ? 2 : 1), "Failed publication leaked an auth token: " + mode);
            if (mode == "final-replace") Check(f.S.Valid(replacement), "Failed candidate displaced another pending login.");
            else Check(f.S.Valid(old), "Failed login invalidated its predecessor: " + mode);
            if (mode is "recovery-failure" or "recovery-ban" or "baseline-failure")
                Check(f.Baseline.Current is null && f.S.PlayerSlot.Writes == 0 && f.S.Loans.FindByIdempotencyKey("pending") is not null,
                    "Early failure ran later login effects.");
            f.S.Recovery.OnPrepare = null; f.BaselineSlot.Fail = f.S.PlayerSlot.Fail = false;
            f.Admin.Unban("user"); f.BeforeLog = null;
            foreach (var raid in f.S.Ledger.ListByType(ServerEventType.Raid)) f.S.Ledger.ChangeStatus(raid.EventId, ServerEventStatus.Cancelled);
            Accepted(f, f.Login.Login(f.Request()), mode == "final-replace" ? replacement.Session! : old.Session);
        }
        Check(f.S.Loans.FindByIdempotencyKey("pending") is null && !Monitor.IsEntered(f.S.Gate), "Successful entry left a pending confirmation or leaked the mutation lock.");
    }

    private static void Accepted(Fixture f, LoginResponse result, string prior)
    {
        Check(result.Result.Accepted && result.AuthenticatedUserId == "user" && result.DisplayName == "user"
            && result.EventQueue is not null && result.WorldMapMarkers is not null && f.Baseline.Current?.ManifestId == "base"
            && !result.IsAdministrator && f.S.Players.FindByUserId("user")?.CurrentSnapshotId == "reported"
            && f.S.Sessions.IsValid("user", "colony", result.SessionId, DateTimeOffset.UtcNow)
            && !f.S.Sessions.IsValid("user", "colony", prior, DateTimeOffset.UtcNow)
            && f.S.Tokens.TryGetPrincipal(result.AuthToken, DateTimeOffset.UtcNow, out var principal)
            && principal!.SessionId == result.SessionId && principal.UserId == "user" && principal.ColonyId == "colony",
            "Independent login lost response data, identity, activity or final session/token binding.");
    }
    private static void Denied(LoginResponse result) => Check(!result.Result.Accepted && result.SessionId is null
        && result.AuthToken is null && result.EventQueue is null && result.WorldMapMarkers is null, "Denied login disclosed prepared session state.");

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly OfflineAccountRegistry Accounts = new();
        internal readonly RecoveryTestSupport.Slot BaselineSlot = new();
        internal readonly CompatibilityBaselineRegistry Baseline;
        internal readonly AdminControlRegistry Admin = new();
        internal readonly LoginService Login;
        internal readonly string Manifest = CompatibilityManifestJsonWriter.Write(EntryTestSupport.Manifest());
        internal Action? BeforeLog;
        internal Fixture()
        {
            Check(Accounts.Authenticate("user", "pw", DateTimeOffset.UtcNow, true).Accepted, "Cannot seed credential.");
            Baseline = new(BaselineSlot);
            var world = new WorldConfigurationRegistry(); var relations = new DiplomacyRelationRegistry();
            var credentials = new AccountService(Accounts, S.Tokens, new RecoveryTestSupport.SteamValidator(), () => S.Configuration);
            var handshakes = new EntryHandshakePolicy(Admin, world, S.Sessions, new(Baseline, world, () => new()));
            var authorization = new ColonySnapshotAuthorization(new ColonyAccessAuthorization(new SessionPrincipalAuthorization(S.Tokens, S.Sessions), relations, new(), S.Ledger, () => true), S.Snapshots);
            var delivery = new EventDeliveryService(authorization, S.Mutations, S.Ledger, S.Events, S.Presence, new SupportDeliveryReconciliation(S.Ledger, S.Players, S.Events));
            var protection = new RaidProtection(S.Ledger, new(), new(), () => S.Configuration, NullLogger.Instance);
            var markers = new WorldMarkerDelivery(S.Players, S.Presence, relations, S.Snapshots, world, new(new ServerPluginRegistry(), Baseline),
                new(), new(S.Ledger, S.Snapshots), protection, new(S.Players, S.Snapshots), () => S.Configuration);
            Login = new(credentials, handshakes, S.Mutations, S.Timeouts, S.Queries, S.Sessions, S.Tokens, world, delivery, S.Activity,
                new(protection, S.Pending), markers, () => { BeforeLog?.Invoke(); return NullLogger.Instance; });
        }
        internal LoginRequest Request() => new(ProtocolApiVersion.Current, "user", "colony", "reported", "", password: "pw", compatibilityManifestJson: Manifest);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
