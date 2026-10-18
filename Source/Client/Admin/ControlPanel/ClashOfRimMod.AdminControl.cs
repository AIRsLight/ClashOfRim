using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
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
        adminAccess.SetAdministrator(value);
        AIRsLight.ClashOfRim.Admin.DeveloperToolAccessPolicy.EnforceCurrentState();
    }

    private void ObserveAdministratorFlag(bool value)
    {
        bool wasAdministrator = adminAccess.IsAdministrator;
        ApplyAdministratorFlag(value);
        if (!value) lastAdminStatus = null;
        else if (!wasAdministrator) StartRefreshAdminStatus();
    }

    internal void StartRefreshAdminStatus()
    {
        var request = adminAccess.Begin(out string? failure);
        if (request is null)
        {
            if (failure is not null) adminStatus = ClashOfRimText.Key(failure);
            return;
        }
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusRefreshing");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                if (!adminAccess.IsCurrent(request)) return;
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    request.Context);
                ClashOfRimClientNetworkResult<ModAdminStatusResponseDto> result = await ClientSessionWork.Observe(client.AdminStatusAsync(request.Request.Scope.Owner.Token));
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult?.Accepted != true)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        (serverResult?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (serverResult?.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                lastAdminStatus = result.Response.IsAdministrator ? ClientProtocolJson.Copy(result.Response) : null;
                adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusRefreshed");
                ApplyAdministratorFlag(result.Response.IsAdministrator);
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin status refresh failed: " + ex);
            }
            finally
            {
                adminAccess.End(request);
            }
        });
    }

    internal void StartUpdateAdminConfiguration(ModAdminConfigurationDto configuration)
    {
        var request = adminAccess.Begin(out string? failure);
        if (request is null)
        {
            if (failure is not null) adminStatus = ClashOfRimText.Key(failure);
            return;
        }
        try { configuration = ClientProtocolJson.Copy(configuration); }
        catch { adminAccess.End(request); throw; }
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSaving");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                if (!adminAccess.IsCurrent(request)) return;
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    request.Context);
                ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto> result =
                    await ClientSessionWork.Observe(client.AdminUpdateConfigurationAsync(configuration, request.Request.Scope.Owner.Token));
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult?.Accepted != true)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        (serverResult?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (serverResult?.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (lastAdminStatus is not null && result.Response.Configuration is not null)
                {
                    lastAdminStatus.Configuration = result.Response.Configuration;
                }

                adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSaved");
                string savedMessage = ClashOfRimText.Key("ClashOfRim.Admin.SavedMessage");
                publication.Dispose();
                await ClientGamePublication.EnqueueAsync(ClientMainThreadDispatcher.Enqueue, request.Request.Scope.Owner.Token,
                    action => adminAccess.TryApply(request, action), () => Messages.Message(savedMessage, MessageTypeDefOf.PositiveEvent));
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin configuration update failed: " + ex);
            }
            finally
            {
                adminAccess.End(request);
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
        var request = adminAccess.Begin(out string? failure);
        if (request is null)
        {
            if (failure is not null) adminStatus = ClashOfRimText.Key(failure);
            return;
        }
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusSendingAction");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                if (!adminAccess.IsCurrent(request)) return;
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    request.Context);
                ClashOfRimClientNetworkResult<ModAdminActionResponseDto> result =
                    await ClientSessionWork.Observe(client.AdminActionAsync(
                        actionKind,
                        targetUserId,
                        targetColonyId,
                        message,
                        notificationSeverity,
                        persistentNotification, request.Request.Scope.Owner.Token));
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                if (!result.Success || result.Response is null)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult?.Accepted != true)
                {
                    adminStatus = ClashOfRimText.Key(
                        "ClashOfRim.Admin.StatusRejected",
                        (serverResult?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                        (serverResult?.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                string completedMessage = serverResult?.Message ?? ClashOfRimText.Key("ClashOfRim.Admin.StatusActionDone");
                adminStatus = completedMessage;
                publication.Dispose();
                await ClientGamePublication.EnqueueAsync(ClientMainThreadDispatcher.Enqueue, request.Request.Scope.Owner.Token,
                    action => adminAccess.TryApply(request, action), () =>
                    {
                        Messages.Message(completedMessage, MessageTypeDefOf.PositiveEvent);
                        if (!adminAccess.IsCurrent(request)) return;
                        adminAccess.End(request);
                        StartRefreshAdminStatus();
                    });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                if (!adminAccess.IsCurrent(request)) return;
                adminStatus = ClashOfRimText.Key(
                    "ClashOfRim.Admin.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Admin action failed: " + ex);
            }
            finally
            {
                adminAccess.End(request);
            }
        });
    }

}
