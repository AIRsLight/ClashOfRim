using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Administration;

// Administrator permission and one in-flight operation belong to the login.
// World writes may additionally bind their source; losing it frees admission
// without letting the old HTTP completion release a newer operation.
internal sealed class ClientAdminAccess
{
    private readonly ClientRequestTracker requests;
    private readonly ClientSessionLifecycle session;
    private Ticket? active;
    private bool administrator;
    internal event Action? Cleared;
    internal event Action<bool>? PermissionChanged;
    internal ClientAdminAccess(ClientSessionLifecycle session)
    {
        this.session = session;
        requests = new(session, () => { active = null; administrator = false; Cleared?.Invoke(); });
    }
    internal T Read<T>(Func<T> read) => requests.Read(read);
    internal ClientRequestTracker.Scope Capture() => requests.Capture();
    internal bool TryApply(ClientRequestTracker.Scope scope, Action action) => requests.TryApply(scope, action);
    internal bool IsAdministrator => requests.Read(() => administrator);
    internal bool Busy => requests.Read(() => { ReleaseExpiredSource(); return active is not null; });
    internal void SetAdministrator(bool value) => requests.Read(() =>
    {
        administrator = value;
        if (!value && active is { } old) End(old);
        PermissionChanged?.Invoke(value);
        return true;
    });
    internal Ticket? Begin(out string? failureKey, Func<bool>? sourceCurrent = null)
    {
        string? failure = null;
        var result = requests.Read(() =>
        {
            ReleaseExpiredSource();
            var scope = requests.Capture();
            if (session.IsUnavailable || !scope.Context.IsConfigured || string.IsNullOrWhiteSpace(scope.Context.AuthToken))
            { failure = "ClashOfRim.Admin.NotConnected"; return null; }
            if (!administrator) { failure = "ClashOfRim.Admin.NotAdministrator"; return null; }
            if (sourceCurrent?.Invoke() == false) return null;
            var request = requests.Begin();
            return request is null ? null : active = new(request, sourceCurrent);
        });
        failureKey = failure;
        return result;
    }
    private void ReleaseExpiredSource()
    { if (active is { } ticket && ticket.SourceCurrent?.Invoke() == false) End(ticket); }
    internal bool TryApply(Ticket ticket, Action action)
    {
        bool applied = false;
        requests.TryApply(ticket.Request, () =>
        {
            if (!administrator || session.IsUnavailable || ticket.SourceCurrent?.Invoke() == false) return;
            action(); applied = true;
        });
        return applied;
    }
    internal bool IsCurrent(Ticket ticket) => TryApply(ticket, static () => { });
    internal void End(Ticket ticket) => requests.TryApply(ticket.Request, () => { requests.End(ticket.Request); if (ReferenceEquals(active, ticket)) active = null; });
    internal sealed record Ticket(ClientRequestTracker.Request Request, Func<bool>? SourceCurrent)
    { internal ClashOfRimClientNetworkContext Context => Request.Context; }
}
