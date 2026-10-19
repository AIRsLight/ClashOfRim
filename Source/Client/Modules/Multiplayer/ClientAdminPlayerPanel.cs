using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal enum ClientAdminPlayerAction { Kick, Ban, Unban, PromoteAdmin, RevokeAdmin, DeletePlayerSave, ResetOfflinePassword }

internal sealed class ClientAdminPlayerPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientAdministration administration;
    private readonly ClientRequestTracker<Boundary> lifetime;
    private readonly HashSet<PasswordDraft> passwords = new();
    private ModAdminStatusResponseDto? observed;
    private Dictionary<(string User, string Colony), Row> targets = new();
    private IReadOnlyList<Row> rows = Array.Empty<Row>();
    private Dictionary<string, Account> accounts = new(StringComparer.Ordinal);
    private IReadOnlyList<Account> accountList = Array.Empty<Account>();

    internal ClientAdminPlayerPanel(ClientSessionLifecycle session, ClientAdminAccess access, ClientAdministration administration)
    {
        this.session = session; this.access = access; this.administration = administration;
        lifetime = new(session, () => new(access.CurrentPermission, session.IsUnavailable),
            boundary => ReferenceEquals(boundary.Permission, access.CurrentPermission) && boundary.Unavailable == session.IsUnavailable, Clear);
    }
    internal object Ownership => lifetime.Capture();
    internal bool IsAvailable => lifetime.Read(() => access.IsAdministrator && !session.IsUnavailable);
    internal bool Busy => access.Busy;
    internal void Close() => lifetime.Reset();
    private void Clear()
    {
        observed = null; targets.Clear(); rows = Array.Empty<Row>();
        accounts.Clear(); accountList = Array.Empty<Account>();
        foreach (var password in passwords) password.Clear();
        passwords.Clear();
    }
    internal IReadOnlyList<Row> Read() => lifetime.Read(() =>
    {
        var source = access.IsAdministrator && !session.IsUnavailable ? administration.Snapshot : null;
        if (ReferenceEquals(source, observed)) return rows;
        var next = new Dictionary<(string User, string Colony), Row>();
        var ordered = new List<Row>();
        var nextAccounts = new Dictionary<string, Account>(StringComparer.Ordinal);
        var orderedAccounts = new List<Account>();
        string currentUser = session.ReadContext().UserId;
        foreach (var player in source?.Players ?? Enumerable.Empty<ModAdminPlayerSummaryDto>())
        {
            var key = (player.UserId, player.ColonyId);
            if (!nextAccounts.TryGetValue(player.UserId, out var account))
            {
                if (!accounts.TryGetValue(player.UserId, out account)) account = new(player.UserId);
                nextAccounts.Add(player.UserId, account);
                if (!string.IsNullOrWhiteSpace(player.UserId)) orderedAccounts.Add(account);
            }
            if (!targets.TryGetValue(key, out var row)) row = new(player, currentUser, account);
            else row.Update(player);
            next[key] = row; ordered.Add(row);
        }
        observed = source; targets = next; rows = ordered.AsReadOnly();
        accounts = nextAccounts; accountList = orderedAccounts.AsReadOnly();
        foreach (var password in passwords.ToArray()) if (!Contains(password.Target)) password.Close();
        return rows;
    });
    internal IReadOnlyList<Account> ReadAccounts() => lifetime.Read(() => { Read(); return accountList; });
    internal bool ContainsAccount(Account account) => lifetime.Read(() =>
    {
        Read();
        return IsAvailable && !string.IsNullOrWhiteSpace(account.UserId)
            && accounts.TryGetValue(account.UserId, out var current) && ReferenceEquals(current, account);
    });
    private bool Contains(Row row) => targets.TryGetValue((row.UserId, row.ColonyId), out var current) && ReferenceEquals(current, row);
    internal bool CanManage(Row row) => lifetime.Read(() => { Read(); return Available(row) && !access.Busy; });
    private bool Available(Row row) => access.IsAdministrator && !session.IsUnavailable && Contains(row) && !row.IsSelf && !string.IsNullOrWhiteSpace(row.UserId);

    internal IReadOnlyList<Intent> PrepareActions(Row row) => lifetime.Read(() =>
    {
        if (!CanManage(row)) return Array.Empty<Intent>();
        var scope = lifetime.Capture();
        var kinds = new[] { ClientAdminPlayerAction.Kick, row.IsBanned ? ClientAdminPlayerAction.Unban : ClientAdminPlayerAction.Ban,
            row.IsAdministrator ? ClientAdminPlayerAction.RevokeAdmin : ClientAdminPlayerAction.PromoteAdmin,
            ClientAdminPlayerAction.DeletePlayerSave, ClientAdminPlayerAction.ResetOfflinePassword };
        return kinds.Where(kind => kind != ClientAdminPlayerAction.DeletePlayerSave || !string.IsNullOrWhiteSpace(row.ColonyId))
            .Select(kind => new Intent(this, scope, row, kind)).ToArray();
    });

    internal PasswordDraft? OpenPassword(Intent intent)
    {
        PasswordDraft? draft = null;
        Apply(intent, false, () =>
        {
            if (intent.Kind != ClientAdminPlayerAction.ResetOfflinePassword) return;
            draft = new(this, intent); passwords.Add(draft);
        });
        return draft;
    }
    private bool Apply(Intent intent, bool requireIdle, Action action)
    {
        bool applied = false;
        lifetime.TryApply(intent.Scope, () =>
        {
            Read();
            if (intent.Consumed || !Available(intent.Target) || (requireIdle && access.Busy)) return;
            // Preserve the action shown by the menu, not a fresh toggle at confirmation time.
            if (!ReferenceEquals(intent.State, intent.Target.StateFor(intent.Kind))) return;
            action(); applied = true;
        });
        return applied;
    }

    internal sealed record Boundary(ClientAdminAccess.Permission Permission, bool Unavailable);
    // Notifications address accounts. Colony-sensitive administration uses Row instead.
    internal sealed class Account
    {
        internal string UserId { get; }
        internal string DisplayName { get; set; }
        internal Account(string user) { UserId = user; DisplayName = user; }
    }
    internal sealed class Row
    {
        internal string UserId { get; }
        internal string ColonyId { get; }
        internal bool IsSelf { get; }
        internal Account Account { get; }
        internal string DisplayName => Account.DisplayName;
        internal bool Online { get; private set; }
        private Flag banned = new(false), administrator = new(false);
        internal bool IsBanned => banned.Value;
        internal bool IsAdministrator => administrator.Value;
        internal object? StateFor(ClientAdminPlayerAction kind) => kind switch
        {
            ClientAdminPlayerAction.Ban or ClientAdminPlayerAction.Unban => banned,
            ClientAdminPlayerAction.PromoteAdmin or ClientAdminPlayerAction.RevokeAdmin => administrator,
            _ => null
        };
        internal Row(ModAdminPlayerSummaryDto player, string currentUser, Account account)
        { UserId = player.UserId; ColonyId = player.ColonyId; Account = account; IsSelf = UserId == currentUser; Update(player); }
        internal void Update(ModAdminPlayerSummaryDto player)
        {
            Account.DisplayName = string.IsNullOrWhiteSpace(player.DisplayName) ? player.UserId : player.DisplayName!;
            Online = player.Online;
            if (banned.Value != player.IsBanned) banned = new(player.IsBanned);
            if (administrator.Value != player.IsAdministrator) administrator = new(player.IsAdministrator);
        }
        private sealed record Flag(bool Value);
    }
    internal sealed class Intent
    {
        private readonly ClientAdminPlayerPanel panel;
        internal ClientRequestTracker<Boundary>.Scope Scope { get; }
        internal Row Target { get; }
        internal ClientAdminPlayerAction Kind { get; }
        internal object? State { get; }
        internal bool Consumed { get; private set; }
        internal Intent(ClientAdminPlayerPanel panel, ClientRequestTracker<Boundary>.Scope scope, Row target, ClientAdminPlayerAction kind)
        { this.panel = panel; Scope = scope; Target = target; Kind = kind; State = target.StateFor(kind); }
        internal bool IsCurrent => panel.Apply(this, false, static () => { });
        internal bool CanExecute => panel.Apply(this, true, static () => { });
        internal void Close() => Consumed = true;
        internal Task? ExecuteAsync(string? message = null)
        {
            Task? work = null;
            panel.Apply(this, true, () =>
            {
                Consumed = true;
                work = panel.administration.ActAsync(new(Kind.ToString(), Target.UserId, Target.ColonyId, message));
            });
            return work;
        }
    }
    internal sealed class PasswordDraft
    {
        private readonly ClientAdminPlayerPanel panel;
        private readonly Intent intent;
        private string value = "";
        internal Row Target => intent.Target;
        internal PasswordDraft(ClientAdminPlayerPanel panel, Intent intent) { this.panel = panel; this.intent = intent; }
        private bool Current { get { if (intent.IsCurrent) return true; Close(); return false; } }
        internal string Value { get { _ = Current; return value; } set { if (Current) this.value = value; } }
        internal bool CanSubmit => Current && intent.CanExecute;
        internal void Clear() { value = ""; intent.Close(); }
        internal void Close() { Clear(); panel.passwords.Remove(this); }
        internal Task? Submit()
        {
            if (!Current) return null;
            var work = intent.ExecuteAsync(value);
            if (work is not null) Close();
            return work;
        }
    }
}
