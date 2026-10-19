using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientAdminConfigurationEditor
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientAdministration administration;
    private readonly ClientRequestTracker<(ClientAdminAccess.Permission Role, bool Unavailable)> lifetime;
    private ModAdminConfigurationDto? observed;
    private string? signature;
    private ClientAdminConfigurationDraft? draft;

    internal ClientAdminConfigurationEditor(ClientSessionLifecycle session, ClientAdminAccess access, ClientAdministration administration)
    {
        this.session = session; this.access = access; this.administration = administration;
        lifetime = new(session, () => (access.CurrentPermission, session.IsUnavailable),
            state => ReferenceEquals(state.Role, access.CurrentPermission) && state.Unavailable == session.IsUnavailable,
            () => { observed = null; signature = null; draft = null; });
    }
    internal bool Busy => access.Busy;
    internal bool IsCurrent(ClientAdminConfigurationDraft candidate) => lifetime.Read(() => ReferenceEquals(Read(), candidate));
    internal bool TryUse(ClientAdminConfigurationDraft candidate, Action action, bool requireIdle = false) => lifetime.Read(() =>
    {
        if (!IsCurrent(candidate) || (requireIdle && access.Busy)) return false;
        action(); return true;
    });
    internal string[]? ReadAcceptedModOrder(ClientAdminConfigurationDraft candidate) => lifetime.Read(() => IsCurrent(candidate)
        ? observed!.CompatibilityMods.OrderBy(mod => mod.LoadOrder).Select(mod => mod.PackageId).ToArray() : null);
    internal void Close() => lifetime.Reset();
    internal ClientAdminConfigurationDraft? Read() => lifetime.Read(() =>
    {
        var source = access.IsAdministrator && !session.IsUnavailable ? administration.Snapshot?.Configuration : null;
        if (source is null) { observed = null; signature = null; return draft = null; }
        if (ReferenceEquals(observed, source)) return draft;
        observed = source;
        var canonical = ClientProtocolJson.Copy(source);
        // Preserve edits on identical refreshes, including an unordered fixed-fee list.
        // Serialize field boundaries instead of culture-sensitive delimiter signatures.
        canonical.FixedTradeFees = canonical.FixedTradeFees.OrderBy(f => f.ThingDefName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ThingDefName, StringComparer.Ordinal).ThenBy(f => f.SilverPerUnit).ToList();
        string current = ClientProtocolJson.Serialize(canonical);
        if (current != signature) { signature = current; draft = new(canonical); }
        return draft;
    });
    internal Task SaveAsync(ClientAdminConfigurationDraft candidate) => lifetime.Read(() =>
        ReferenceEquals(Read(), candidate) && !access.Busy && candidate.TryBuild(out var configuration)
            ? administration.SaveAsync(configuration) : Task.CompletedTask);

    internal Action<string> SelectFeeStrategy(ClientAdminConfigurationDraft candidate)
        => Bind(candidate, value => candidate.TradeFeeStrategy = TradeFeeStrategies.Normalize(value));
    internal Action<string> AddFee(ClientAdminConfigurationDraft candidate)
        => Bind(candidate, value => candidate.FixedTradeFees.Add(new(value, "0")));
    internal Action<string> SelectFeeThing(ClientAdminConfigurationDraft candidate, ClientAdminFixedTradeFeeInput row)
        => Bind(candidate, value => { if (candidate.FixedTradeFees.Contains(row)) row.ThingDefName = value; });
    internal Action<string> AddPenalty(ClientAdminConfigurationDraft candidate)
        => Bind(candidate, value => candidate.PenaltyStages.Add(new("4", value, "1")));
    internal Action<string> SelectPenaltyKind(ClientAdminConfigurationDraft candidate, ClientAdminPenaltyInput row)
        => Bind(candidate, value => { if (candidate.PenaltyStages.Contains(row)) row.Kind = value; });
    internal Action<string> SelectModRole(ClientAdminConfigurationDraft candidate, ModAdminCompatibilityModDto mod)
        => Bind(candidate, value => { if (candidate.CompatibilityMods.Contains(mod)) mod.Role = value; });
    internal Action<string> SelectConfigMode(ClientAdminConfigurationDraft candidate, ModAdminCompatibilityModDto mod, ModAdminCompatibilityConfigDto config)
        => Bind(candidate, value => { if (candidate.CompatibilityMods.Contains(mod) && mod.Configs.Contains(config)) config.Mode = value; });

    private Action<string> Bind(ClientAdminConfigurationDraft candidate, Action<string> edit)
        => value => TryUse(candidate, () => edit(value));
}
