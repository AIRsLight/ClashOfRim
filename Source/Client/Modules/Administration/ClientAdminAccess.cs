using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Administration;

// One admission serializes authenticated administration and credential recovery.
// World writes bind their source; recovery binds its credential and availability.
// Losing either frees admission without letting old completion release new work.
internal sealed class ClientAdminAccess
{
    private readonly ClientRequestTracker requests;
    private readonly ClientSessionLifecycle session;
    private Ticket? active;
    private Permission permission = new(false);
    internal event Action? Cleared;
    internal event Action<bool>? PermissionChanged;
    internal ClientAdminAccess(ClientSessionLifecycle session)
    {
        this.session = session;
        requests = new(session, () => { active = null; permission = new(false); Cleared?.Invoke(); });
    }
    internal T Read<T>(Func<T> read) => requests.Read(read);
    internal ClientRequestTracker.Scope Capture() => requests.Capture();
    internal bool TryApply(ClientRequestTracker.Scope scope, Action action) => requests.TryApply(scope, action);
    internal Permission CurrentPermission => requests.Read(() => permission);
    internal bool IsAdministrator => CurrentPermission.Allowed;
    internal bool Busy => requests.Read(() => { ReleaseExpiredSource(); return active is not null; });
    internal void SetAdministrator(bool value) => requests.Read(() =>
    {
        if (permission.Allowed != value) permission = new(value);
        if (!value && active is { } old) End(old);
        PermissionChanged?.Invoke(value);
        return true;
    });
    internal Ticket? Begin(out string? failureKey, Func<bool>? sourceCurrent = null)
        => Begin(out failureKey, sourceCurrent, requiresAdministrator: true);
    // Compatibility recovery must work before login. The server authenticates
    // the supplied account credential; this ticket grants no local admin role.
    internal Ticket? BeginCredentials(out string? failureKey)
        => Begin(out failureKey, null, requiresAdministrator: false);
    private Ticket? Begin(out string? failureKey, Func<bool>? sourceCurrent, bool requiresAdministrator)
    {
        string? failure = null;
        var result = requests.Read(() =>
        {
            ReleaseExpiredSource();
            var context = session.ReadContext();
            if (requiresAdministrator
                ? session.IsUnavailable || !context.IsConfigured || string.IsNullOrWhiteSpace(context.AuthToken) : !context.HasServerIdentity)
            { failure = "ClashOfRim.Admin.NotConnected"; return null; }
            if (requiresAdministrator && !permission.Allowed) { failure = "ClashOfRim.Admin.NotAdministrator"; return null; }
            if (sourceCurrent?.Invoke() == false) return null;
            var request = requests.Begin();
            return request is null ? null : active = new(request, sourceCurrent, requiresAdministrator, session.IsUnavailable);
        });
        failureKey = failure;
        return result;
    }
    private void ReleaseExpiredSource()
    { if (active is { } ticket && !OwnsSource(ticket)) End(ticket); }
    private bool OwnsSource(Ticket ticket)
    {
        if (ticket.SourceCurrent?.Invoke() == false) return false;
        if (ticket.RequiresAdministrator) return true;
        var current = session.ReadContext();
        return session.IsUnavailable == ticket.UnavailableAtStart
            && current.OfflinePassword == ticket.Context.OfflinePassword && current.SteamAuthTicket == ticket.Context.SteamAuthTicket;
    }
    internal bool TryApply(Ticket ticket, Action action)
    {
        bool applied = false;
        requests.TryApply(ticket.Request, () =>
        {
            if ((ticket.RequiresAdministrator && (!permission.Allowed || session.IsUnavailable)) || !OwnsSource(ticket)) return;
            action(); applied = true;
        });
        return applied;
    }
    internal bool IsCurrent(Ticket ticket) => TryApply(ticket, static () => { });
    internal void End(Ticket ticket) => requests.TryApply(ticket.Request, () => { requests.End(ticket.Request); if (ReferenceEquals(active, ticket)) active = null; });
    internal sealed record Ticket(ClientRequestTracker.Request Request, Func<bool>? SourceCurrent, bool RequiresAdministrator, bool UnavailableAtStart)
    { internal ClashOfRimClientNetworkContext Context => Request.Context; }
    // Retain the actual grant identity across repeated observations of the same role.
    // Revocation/regrant must not revive a menu or draft created under the old grant.
    internal sealed record Permission(bool Allowed);
}
