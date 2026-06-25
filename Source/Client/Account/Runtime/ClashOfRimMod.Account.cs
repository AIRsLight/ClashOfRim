using System;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private bool accountPasswordInProgress;
    private string accountPasswordStatus = string.Empty;

    internal bool AccountPasswordInProgress => accountPasswordInProgress;

    internal string AccountPasswordStatus => string.IsNullOrWhiteSpace(accountPasswordStatus)
        ? ClashOfRimText.Key("ClashOfRim.Account.PasswordStatusIdle")
        : accountPasswordStatus;

    internal void StartChangeOfflinePassword(string currentPassword, string newPassword)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            accountPasswordStatus = ClashOfRimText.Key("ClashOfRim.Account.PasswordNoSession");
            Messages.Message(accountPasswordStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (accountPasswordInProgress)
        {
            return;
        }

        accountPasswordInProgress = true;
        accountPasswordStatus = ClashOfRimText.Key("ClashOfRim.Account.PasswordChanging");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto> result =
                    await client.ChangeOfflinePasswordAsync(currentPassword, newPassword);
                if (!result.Success || result.Response?.Result is null)
                {
                    accountPasswordStatus = ClashOfRimText.Key(
                        "ClashOfRim.Account.PasswordFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (!result.Response.Result.Accepted)
                {
                    accountPasswordStatus = ClashOfRimText.Key(
                        "ClashOfRim.Account.PasswordRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        (result.Response.Result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                settings.OfflinePassword = newPassword ?? string.Empty;
                settings.Write();
                accountPasswordStatus = result.Response.Result.Message ?? ClashOfRimText.Key("ClashOfRim.Account.PasswordChanged");
                EnqueueClashOfRimMainThreadAction(() =>
                    Messages.Message(accountPasswordStatus, MessageTypeDefOf.PositiveEvent, historical: false));
            }
            catch (Exception ex)
            {
                accountPasswordStatus = ClashOfRimText.Key(
                    "ClashOfRim.Account.PasswordException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Offline password change failed: " + ex);
            }
            finally
            {
                accountPasswordInProgress = false;
            }
        });
    }
}
