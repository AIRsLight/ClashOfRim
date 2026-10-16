using System.Reflection;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using static RecoveryTestSupport;

internal static class EntryAuthenticationTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        int count = 0;
        // Immediate legacy callers and deferred entry callers must obey the same identity/provider rules.
        foreach (bool immediate in new[] { false, true })
        foreach (string mode in new[] { "offline", "missing", "password", "lockout", "debug", "key", "identity", "trimmed", "denied", "config-change" })
        {
            var now = DateTimeOffset.UtcNow; var accounts = new OfflineAccountRegistry(); var steam = new SteamValidator();
            accounts.Authenticate("receiver", "pw", now, true);
            bool useSteam = mode is "debug" or "key" or "identity" or "trimmed" or "denied" or "config-change";
            var state = new ClashOfRimNetworkState(offlineAccounts: accounts, steamAuthTickets: steam);
            if (useSteam) state.UpdateServerConfiguration(mode == "key" ? new(steamWebApiKey: "test") : new(authenticationDebugMode: true));
            if (mode == "lockout") for (int i = 0; i < 5; i++) accounts.Authenticate("receiver", "bad", now);
            steam.Identity = mode == "identity" ? "other" : mode == "trimmed" ? " receiver " : "receiver";
            steam.Deny = mode == "denied";
            if (mode == "config-change") steam.Before = () => state.UpdateServerConfiguration(new());
            string user = mode == "missing" ? "missing" : mode == "trimmed" ? " receiver " : "receiver";
            string password = mode == "password" ? "bad" : "pw";
            AuthenticationValidationResult result;
            if (immediate)
            {
                var method = typeof(ClashOfRimNetworkServer).GetMethod("ValidateAuthentication", BindingFlags.NonPublic | BindingFlags.Static)!;
                result = (AuthenticationValidationResult)method.Invoke(null, [state, user, null, password, now, false])!;
            }
            else
                result = new CredentialAuthentication(accounts, steam, () => state.ServerConfiguration).Prepare(user, null, password, now).Validate(now, false);

            bool accepted = mode is "offline" or "debug" or "key" or "trimmed";
            Require(result.Accepted == accepted && steam.Calls == (useSteam ? 1 : 0), $"Credential path differs: {immediate}/{mode}");
            if (accepted) Require(result.AuthenticatedUserId == "receiver" && result.Rejection is null, "Successful credential identity was not normalized.");
            else Require(result.AuthenticatedUserId is null && result.Rejection?.ErrorCode == (mode == "missing" ? ProtocolErrorCode.AccountNotFound : ProtocolErrorCode.Unauthorized), "Rejected credentials lost their error or disclosed identity.");
            count++;
        }

        var slot = new Slot(); var registry = new OfflineAccountRegistry(slot);
        var configuration = new ClashOfRimServerConfiguration();
        var service = new CredentialAuthentication(registry, new SteamValidator(), () => configuration);
        var at = DateTimeOffset.UtcNow;
        var candidate = service.Prepare("receiver", null, "pw", at, createAccount: true);
        Require(candidate.Result.Accepted && candidate.Revalidate() is null && slot.Read() is null, "Preparing credentials registered an account.");
        slot.Fail = true; bool failed = false;
        try { candidate.Commit(at); } catch (IOException) { failed = true; }
        Require(failed && slot.Read() is null && candidate.Revalidate() is null, "Failed account commit poisoned the shared proof.");
        slot.Fail = false; Require(candidate.Commit(at).Accepted && registry.Authenticate("receiver", "pw", at).Accepted, "Shared proof could not commit after retry.");
        int writes = slot.Writes; Require(candidate.Commit(at).Accepted && slot.Writes == writes, "Repeated credential commit rewrote the account."); count++;
        registry.ResetPassword("receiver", "pw", at, out _);
        Require(!candidate.Validate(at, false).Accepted && !candidate.Commit(at).Accepted, "Shared proof survived same-password account replacement."); count++;
        var steamProof = new CredentialAuthentication(registry, new SteamValidator(), () => configuration);
        configuration = new(authenticationDebugMode: true);
        var ticket = steamProof.Prepare("receiver", null, null, at);
        configuration = new(authenticationDebugMode: true, steamAppId: 1);
        Require(!ticket.Validate(at, false).Accepted && !ticket.Commit(at).Accepted, "Shared Steam proof survived policy replacement."); count++;
        Console.WriteLine($"Unified credential scenarios passed: {count}");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
