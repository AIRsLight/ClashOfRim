using System.Globalization;
using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Accounts;

internal interface IClientPasswordTransport
{
    Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>> ChangeAsync(
        ClashOfRimClientNetworkContext context, string? currentPassword, string? newPassword, CancellationToken token);
}
internal interface IClientPasswordStore
{
    void Write(string password);
    void Persist();
}
internal enum ClientPasswordStage { StatusIdle, NoSession, Changing, Failed, Rejected, Changed, Exception, SaveFailed }
internal sealed record ClientPasswordNotice(ClientPasswordStage Stage, string? Code = null, string? Message = null, Exception? Exception = null);
internal interface IClientPasswordPresentation
{
    string Format(ClientPasswordNotice notice);
    void Report(ClientPasswordNotice notice, string message);
}

internal sealed class ClientPasswordChange
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker requests;
    private readonly IClientPasswordTransport transport;
    private readonly IClientPasswordStore store;
    private readonly IClientPasswordPresentation presentation;
    private readonly Action<Action, Action> enqueue;
    private string message = "";

    internal ClientPasswordChange(ClientSessionLifecycle session, IClientPasswordTransport transport, IClientPasswordStore store,
        IClientPasswordPresentation presentation, Action<Action, Action> enqueue)
    {
        this.session = session; this.transport = transport; this.store = store; this.presentation = presentation; this.enqueue = enqueue;
        requests = new(session, () => message = presentation.Format(new(ClientPasswordStage.StatusIdle)));
    }
    internal bool Busy => requests.Busy;
    internal string Message => requests.Read(() => message);

    // Called from the UI. Freeze credentials before launching any background work.
    internal Task StartAsync(string? currentPassword, string? newPassword)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (session.IsUnavailable || !scope.Context.IsConfigured || string.IsNullOrWhiteSpace(scope.Context.AuthToken))
        {
            Notice(new(ClientPasswordStage.NoSession), effect => requests.TryApply(scope, effect));
            return Task.CompletedTask;
        }
        var request = requests.Begin();
        if (request is null) return Task.CompletedTask;
        try
        {
            Notice(new(ClientPasswordStage.Changing), effect => Apply(request, effect));
            return ClientSessionWork.Run(() => ExecuteAsync(request, currentPassword, newPassword));
        }
        catch { requests.End(request); throw; }
    }

    private async Task ExecuteAsync(ClientRequestTracker.Request request, string? currentPassword, string? newPassword)
    {
        bool accepted = false, saved = false;
        try
        {
            if (!Apply(request, static () => { })) return;
            var result = await transport.ChangeAsync(request.Context, currentPassword, newPassword, request.Scope.Owner.Token).ConfigureAwait(false);
            var receipt = result.Response?.Result;
            var notice = !result.Success || receipt is null
                ? new ClientPasswordNotice(ClientPasswordStage.Failed, result.ErrorCode, result.Message)
                : !receipt.Accepted ? new(ClientPasswordStage.Rejected, receipt.ErrorCode.ToString(CultureInfo.InvariantCulture), receipt.Message)
                : new(ClientPasswordStage.Changed, Message: receipt.Message);
            accepted = notice.Stage == ClientPasswordStage.Changed;
            await Publish(request, () =>
            {
                if (accepted && (!Apply(request, () => store.Write(newPassword ?? "")) || !Apply(request, store.Persist))) return;
                saved = accepted;
                Notice(notice, effect => Apply(request, effect));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Publish(request, () => Notice(new(accepted && !saved ? ClientPasswordStage.SaveFailed : ClientPasswordStage.Exception,
                Exception: ex), effect => Apply(request, effect))).ConfigureAwait(false);
        }
        finally { requests.End(request); }
    }
    private Task Publish(ClientRequestTracker.Request request, Action action) => ClientGamePublication.EnqueueAsync(enqueue,
        request.Scope.Owner.Token, effect => Apply(request, effect), action);
    private bool Apply(ClientRequestTracker.Request request, Action action)
    {
        bool applied = false;
        requests.TryApply(request, () =>
        {
            if (session.IsUnavailable) return;
            action(); applied = true;
        });
        return applied;
    }
    private void Notice(ClientPasswordNotice notice, Func<Action, bool> apply)
    {
        string text = "";
        if (!apply(() => text = presentation.Format(notice)) || !apply(() => message = text)) return;
        apply(() => presentation.Report(notice, text));
    }
}
