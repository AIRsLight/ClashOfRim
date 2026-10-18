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

    private void ObserveAdministratorFlag(bool value)
    {
        bool wasAdministrator = isAdministrator;
        ApplyAdministratorFlag(value);
        if (!value) lastAdminStatus = null;
        else if (!wasAdministrator) StartRefreshAdminStatus();
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
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminStatusResponseDto> result = await ClientSessionWork.Observe(client.AdminStatusAsync());
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin status refresh failed: " + ex);
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    adminInProgress = false;
                });
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
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto> result =
                    await ClientSessionWork.Observe(client.AdminUpdateConfigurationAsync(configuration));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin configuration update failed: " + ex);
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    adminInProgress = false;
                });
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
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAdminActionResponseDto> result =
                    await ClientSessionWork.Observe(client.AdminActionAsync(
                        actionKind,
                        targetUserId,
                        targetColonyId,
                        message,
                        notificationSeverity,
                        persistentNotification));
                using var publication = ClientSessionWork.EnterPublication();
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin action failed: " + ex);
            }
            finally
            {
                ClientSessionWork.TryApply(() =>
                {
                    adminInProgress = false;
                });
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
