using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Administration;

internal sealed record ClientAdminCommand(string Kind, string? User = null, string? Colony = null,
    string? Message = null, string? Severity = null, bool Persistent = true);
internal interface IClientAdminTransport
{
    Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto configuration, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token);
}
internal interface IClientAdminPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Success(string message);
    void Diagnose(string message);
    void EnforcePermission();
    void Enqueue(Action action, Action discard);
}

// Admin control is account work. The shared access module owns admission;
// this module owns the accepted cache, command order and user-visible results.
internal sealed class ClientAdministration
{
    private readonly ClientAdminAccess access;
    private readonly IClientAdminTransport transport;
    private readonly IClientAdminPresentation presentation;
    private string message = "";
    private ModAdminStatusResponseDto? snapshot;
    internal ClientAdministration(ClientAdminAccess access, IClientAdminTransport transport, IClientAdminPresentation presentation)
    {
        this.access = access; this.transport = transport; this.presentation = presentation;
        access.Cleared += Clear;
        access.PermissionChanged += value => { if (!value) snapshot = null; };
    }
    internal string Message { get => access.Read(() => message.Length == 0 ? presentation.Text("ClashOfRim.Admin.StatusIdle") : message); set => access.Read(() => message = value); }
    // UI consumers borrow an accepted view for reading and build separate edit DTOs.
    // Refresh/save replace the view; transports and caller edit DTOs cannot mutate it.
    internal ModAdminStatusResponseDto? Snapshot => access.Read(() => snapshot);
    internal void Reset() => access.Read(() => { Clear(); return true; });
    private void Clear() { snapshot = null; message = ""; }
    internal Task ObservePermission(bool value, bool refresh = false, Func<bool>? current = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (current?.Invoke() == false) return Task.CompletedTask;
        var scope = access.Capture(); bool previous = access.IsAdministrator;
        Task work = Task.CompletedTask;
        bool Apply(Action action)
        {
            bool applied = false;
            access.TryApply(scope, () => { if (current?.Invoke() == false) return; action(); applied = true; });
            return applied;
        }
        if (!Apply(() => access.SetAdministrator(value))) return work;
        if (!Apply(presentation.EnforcePermission)) return work;
        Apply(() => { if (refresh && value && !previous && access.IsAdministrator) work = RefreshAsync(); });
        return work;
    }
    internal Task RefreshAsync() => StartAsync("ClashOfRim.Admin.StatusRefreshing", static () => true,
        (ticket, _) => transport.ReadAsync(ticket.Context, ticket.Request.Scope.Owner.Token), response => response.Result, PublishRead);
    internal Task SaveAsync(ModAdminConfigurationDto configuration) => StartAsync("ClashOfRim.Admin.StatusSaving", () => ClientProtocolJson.Copy(configuration),
        (ticket, captured) => transport.SaveAsync(ticket.Context, captured, ticket.Request.Scope.Owner.Token), response => response.Result, PublishSave);
    internal Task ActAsync(ClientAdminCommand command) => StartAsync("ClashOfRim.Admin.StatusSendingAction", () => command,
        (ticket, captured) => transport.ActAsync(ticket.Context, captured, ticket.Request.Scope.Owner.Token), response => response.Result, PublishAction, refreshAfter: true);

    private async Task StartAsync<TInput, TResponse>(string startingKey, Func<TInput> freeze,
        Func<ClientAdminAccess.Ticket, TInput, Task<ClashOfRimClientNetworkResult<TResponse>>> send,
        Func<TResponse, ModProtocolResponseDto?> protocol, Func<ClientAdminAccess.Ticket, TResponse, bool> publish, bool refreshAfter = false) where TResponse : class
    {
        var scope = access.Capture();
        var ticket = access.Begin(out string? failure);
        if (ticket is null)
        {
            if (failure is not null)
            {
                string text = presentation.Text(failure);
                access.TryApply(scope, () => { if (!access.Busy) message = text; });
            }
            return;
        }
        try
        {
            var input = freeze();
            if (!Report(ticket, () => presentation.Text(startingKey))) return;
            await ClientSessionWork.Run(async () =>
            {
                if (!access.IsCurrent(ticket)) return;
                var result = await send(ticket, input).ConfigureAwait(false);
                if (!access.IsCurrent(ticket)) return;
                if (!result.Success || result.Response is null)
                {
                    await Publish(ticket, () => Report(ticket, () => presentation.Text("ClashOfRim.Admin.StatusFailed", ("CODE", result.ErrorCode ?? ""), ("MESSAGE", result.Message ?? "")))).ConfigureAwait(false);
                    return;
                }
                var response = ClientProtocolJson.Copy(result.Response);
                var receipt = protocol(response);
                if (receipt?.Accepted != true)
                {
                    await Publish(ticket, () => Report(ticket, () => presentation.Text("ClashOfRim.Admin.StatusRejected", ("CODE", receipt?.ErrorCode.ToString() ?? ""), ("MESSAGE", receipt?.Message ?? "")))).ConfigureAwait(false);
                    return;
                }
                bool completed = false;
                await Publish(ticket, () => completed = publish(ticket, response)).ConfigureAwait(false);
                if (completed && refreshAfter)
                {
                    Task follow = Task.CompletedTask;
                    access.TryApply(ticket, () => { access.End(ticket); follow = RefreshAsync(); });
                    await follow.ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Publish(ticket, () =>
            {
                if (Report(ticket, () => presentation.Text("ClashOfRim.Admin.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message))))
                    access.TryApply(ticket, () => presentation.Diagnose("Administrator request failed: " + ex));
            }).ConfigureAwait(false);
        }
        finally { access.End(ticket); }
    }
    private bool PublishRead(ClientAdminAccess.Ticket ticket, ModAdminStatusResponseDto response)
    {
        if (!Step(ticket, () => snapshot = response.IsAdministrator ? response : null)
            || !Report(ticket, () => presentation.Text("ClashOfRim.Admin.StatusRefreshed"))) return false;
        return access.TryApply(ticket, () => ObservePermission(response.IsAdministrator));
    }
    private bool PublishSave(ClientAdminAccess.Ticket ticket, ModAdminUpdateConfigurationResponseDto response)
    {
        if (!Step(ticket, () =>
        {
            if (snapshot is null || response.Configuration is null) return;
            var updated = ClientProtocolJson.Copy(snapshot);
            updated.Configuration = response.Configuration;
            snapshot = updated;
        }) || !Report(ticket, () => presentation.Text("ClashOfRim.Admin.StatusSaved"))) return false;
        string text = "";
        return Step(ticket, () => text = presentation.Text("ClashOfRim.Admin.SavedMessage")) && Step(ticket, () => presentation.Success(text));
    }
    private bool PublishAction(ClientAdminAccess.Ticket ticket, ModAdminActionResponseDto response)
    {
        string text = "";
        return Step(ticket, () => text = response.Result!.Message ?? presentation.Text("ClashOfRim.Admin.StatusActionDone"))
            && Report(ticket, () => text) && Step(ticket, () => presentation.Success(text));
    }
    private bool Report(ClientAdminAccess.Ticket ticket, Func<string> format)
    {
        string text = "";
        return Step(ticket, () => text = format()) && Step(ticket, () => message = text);
    }
    private bool Step(ClientAdminAccess.Ticket ticket, Action action) => access.TryApply(ticket, action) && access.IsCurrent(ticket);
    private Task Publish(ClientAdminAccess.Ticket ticket, Action action)
    {
        bool consumed = false;
        return ClientGamePublication.EnqueueAsync(presentation.Enqueue, ticket.Request.Scope.Owner.Token,
            effect => access.TryApply(ticket, effect), () => { if (consumed) return; consumed = true; action(); });
    }
}
