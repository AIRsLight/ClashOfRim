using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal enum ClientAdminBroadcastSeverity { Info, Warning, Critical }

internal sealed class ClientAdminMaintenancePanel
{
    private readonly ClientAdminPlayerPanel players;
    private readonly ClientAdministration administration;
    private readonly ClientRequestTracker<object> lifetime;
    private Draft draft = new();

    internal ClientAdminMaintenancePanel(ClientSessionLifecycle session, ClientAdminPlayerPanel players, ClientAdministration administration)
    {
        this.players = players; this.administration = administration;
        // The shared player model already owns the administrator grant/session boundary.
        lifetime = new(session, () => players.Ownership, owner => ReferenceEquals(owner, players.Ownership), () => draft = new());
    }
    internal Draft Read() => lifetime.Read(() => draft);
    internal bool Busy => players.Busy;
    internal bool? LoginLocked => players.IsAvailable ? administration.Snapshot?.MaintenanceLoginLocked : null;
    internal void Close() => lifetime.Reset();
    internal IReadOnlyList<ClientAdminPlayerPanel.Account> Targets() => players.ReadAccounts()
        .OrderBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(player => player.UserId, StringComparer.Ordinal).ToArray();

    internal Action<ClientAdminPlayerPanel.Account?> ChooseTarget(Draft candidate) => Bind<ClientAdminPlayerPanel.Account?>(candidate, value => candidate.Target = value);
    internal Action<ClientAdminBroadcastSeverity> ChooseSeverity(Draft candidate) => Bind<ClientAdminBroadcastSeverity>(candidate, value =>
        candidate.Severity = value is ClientAdminBroadcastSeverity.Warning or ClientAdminBroadcastSeverity.Critical ? value : ClientAdminBroadcastSeverity.Info);
    private Action<T> Bind<T>(Draft candidate, Action<T> change)
    {
        var owner = lifetime.Capture();
        return value => lifetime.TryApply(owner, () =>
        { if (ReferenceEquals(Read(), candidate) && players.IsAvailable) change(value); });
    }
    internal bool CanSubmit(Draft candidate) => lifetime.Read(() => ReferenceEquals(Read(), candidate) && players.IsAvailable && !players.Busy);
    internal bool CanBroadcast(Draft candidate) => CanSubmit(candidate) && (candidate.Target is null || players.ContainsAccount(candidate.Target));
    internal Task? BroadcastAsync(Draft candidate) => lifetime.Read(() => CanBroadcast(candidate)
        // The server routes notifications by account; colony IDs do not constrain delivery.
        ? administration.ActAsync(new("Broadcast", User: candidate.Target?.UserId, Message: candidate.Message,
            Severity: candidate.Severity.ToString(), Persistent: candidate.Persistent)) : null);
    internal Task? SetMaintenanceAsync(Draft candidate, bool locked) => lifetime.Read(() => CanSubmit(candidate)
        ? administration.ActAsync(new(locked ? "LockMaintenance" : "UnlockMaintenance", Message: locked ? candidate.Reason : null)) : null);

    internal sealed class Draft
    {
        internal string Message = "", Reason = "";
        internal bool Persistent = true;
        internal ClientAdminBroadcastSeverity Severity = ClientAdminBroadcastSeverity.Info;
        // Keep an unavailable target selected so a stale menu can never widen delivery to everyone.
        internal ClientAdminPlayerPanel.Account? Target;
    }
}
