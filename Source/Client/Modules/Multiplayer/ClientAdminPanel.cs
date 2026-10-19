using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal enum ClientAdminTab { Trade, Shop, Bank, Mercenary, Raid, Players, Maintenance, Manifest, Audit }

// One selected administrator page owns navigation and refresh cadence. Accepted
// data, request admission and action follow-ups remain in ClientAdministration.
internal sealed class ClientAdminPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientAdministration administration;
    private readonly ClientRequestTracker<(ClientAdminAccess.Permission Grant, bool Unavailable)> lifetime;
    private readonly PanelRefreshSchedule refresh = new();
    private ClientAdminTab selected = ClientAdminTab.Trade;
    internal ClientAdminPanel(ClientSessionLifecycle session, ClientAdminAccess access, ClientAdministration administration)
    {
        this.session = session; this.access = access; this.administration = administration;
        lifetime = new(session, () => (access.CurrentPermission, session.IsUnavailable),
            boundary => ReferenceEquals(boundary.Grant, access.CurrentPermission) && boundary.Unavailable == session.IsUnavailable,
            refresh.Request);
    }
    internal object Ownership => lifetime.Capture();
    internal bool IsAvailable => lifetime.Read(() => access.IsAdministrator && !session.IsUnavailable);
    internal bool CanRefresh => IsAvailable && !access.Busy;
    internal string Status => lifetime.Read(() => IsAvailable ? administration.Message : "");
    internal ClientAdminTab Selected => lifetime.Read(() => selected);
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => { refresh.Request(); return true; });
    internal Task RefreshAsync(float now, bool manual = false) => lifetime.Read(() =>
    {
        if (!IsAvailable || !refresh.TryStart(now, access.Busy, manual)) return Task.CompletedTask;
        return administration.RefreshAsync();
    });
    internal Action Select(ClientAdminTab tab)
    {
        var owner = lifetime.Capture();
        return () => lifetime.TryApply(owner, () => { if (IsAvailable) selected = tab; });
    }
}
