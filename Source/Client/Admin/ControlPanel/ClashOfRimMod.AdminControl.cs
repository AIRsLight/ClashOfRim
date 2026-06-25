using System;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void ApplyAdministratorFlag(bool value)
    {
        isAdministrator = value;
        AIRsLight.ClashOfRim.Admin.DeveloperToolAccessPolicy.EnforceCurrentState();
    }

    internal void StartRefreshAdminStatus()
    {
        if (!CanRunAdminRequest(out string failure))
        {
            adminStatus = failure;
            return;
        }

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminStatusResponseDto> result = await client.AdminStatusAsync();
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ApplyAdministratorFlag(result.Response.IsAdministrator);
                lastAdminStatus = result.Response;
                adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusRefreshed");
            }
            catch (Exception ex)
            {
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin status refresh failed: " + ex);
            }
            finally
            {
                adminInProgress = false;
            }
        });
    }

    internal void StartUpdateAdminConfiguration(ModAdminConfigurationDto configuration)
    {
        if (!CanRunAdminRequest(out string failure))
        {
            adminStatus = failure;
            return;
        }

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSaving");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto> result =
                    await client.AdminUpdateConfigurationAsync(configuration);
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (lastAdminStatus is not null && result.Response.Configuration is not null)
                {
                    lastAdminStatus.Configuration = result.Response.Configuration;
                }

                adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSaved");
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(ClashOfRimText.Key("ClashOfRim.Admin.SavedMessage"), MessageTypeDefOf.PositiveEvent));
            }
            catch (Exception ex)
            {
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin configuration update failed: " + ex);
            }
            finally
            {
                adminInProgress = false;
            }
        });
    }

    internal void StartAdminAction(
        string actionKind,
        string? targetUserId = null,
        string? targetColonyId = null,
        string? message = null,
        string? notificationSeverity = null,
        bool persistentNotification = true)
    {
        if (!CanRunAdminRequest(out string failure))
        {
            adminStatus = failure;
            return;
        }

        adminInProgress = true;
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSendingAction");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminActionResponseDto> result =
                    await client.AdminActionAsync(
                        actionKind,
                        targetUserId,
                        targetColonyId,
                        message,
                        notificationSeverity,
                        persistentNotification);
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        serverResult.ErrorCode.Named("CODE"),
                        (serverResult.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                adminStatus = serverResult?.Message ?? ClashOfRimText.Key("ClashOfRim.Admin.StatusActionDone");
                StartRefreshAdminStatus();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(adminStatus, MessageTypeDefOf.PositiveEvent));
            }
            catch (Exception ex)
            {
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin action failed: " + ex);
            }
            finally
            {
                adminInProgress = false;
            }
        });
    }

    private bool CanRunAdminRequest(out string failure)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            failure = ClashOfRimText.Key("ClashOfRim.Admin.NotConnected");
            return false;
        }

        if (!isAdministrator)
        {
            failure = ClashOfRimText.Key("ClashOfRim.Admin.NotAdministrator");
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
