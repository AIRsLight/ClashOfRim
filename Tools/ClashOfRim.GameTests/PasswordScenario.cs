using AIRsLight.ClashOfRim.Network;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class PasswordScenario(TestRun run, ScenarioPolling polling, OfflineAccountRegistry accounts)
{
    internal async Task Change()
    {
        const string initial = "local-test-password", replacement = "rimtest-replacement-password";
        try
        {
            await Set("incorrect-password", replacement);
            Check(accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Rejected password change altered credentials.");
            await Set(initial, replacement);
            Check(!accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Old password still accepted.");
            Check(accounts.Authenticate("rimtest-B", replacement, DateTimeOffset.UtcNow).Accepted, "New password rejected.");
        }
        finally
        {
            await Set(replacement, initial);
            Check(accounts.Authenticate("rimtest-B", initial, DateTimeOffset.UtcNow).Accepted, "Password restore failed.");
        }
        await run.RecordAsync(new { serverObservation = "password-change", wrongCurrentRejected = true, oldRejected = true, replacementAccepted = true, restored = true });
    }
    private async Task Set(string currentPassword, string newPassword)
    {
        await run.InvokeClientAsync("B", "StartChangeOfflinePassword", new { currentPassword, newPassword });
        await polling.Wait("B", "clash.state", null, s => !s.Bool("accountPasswordInProgress"));
    }
}
