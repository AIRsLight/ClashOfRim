using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal interface IClientAdminManifestSource
{
    string SteamPostfix { get; }
    IReadOnlyList<string>? ReadPackageOrder();
}

// Configuration edits and delayed operations share the editor's exact draft ownership.
// The existing domain modules still own admission, capture, transport and publication.
internal sealed class ClientAdminManifestPanel
{
    internal ClientAdminConfigurationEditor Configuration { get; }
    private readonly IClientAdminManifestSource source;
    private readonly ClientCompatibilityOverride compatibility;
    private readonly ClientWorldWrites world;
    private readonly Func<ModColonyAppearanceDto?> appearance;
    internal ClientAdminManifestPanel(ClientAdminConfigurationEditor configuration, IClientAdminManifestSource source,
        ClientCompatibilityOverride compatibility, ClientWorldWrites world, Func<ModColonyAppearanceDto?> appearance)
    { Configuration = configuration; this.source = source; this.compatibility = compatibility; this.world = world; this.appearance = appearance; }

    internal Override? PrepareOverride(ClientAdminConfigurationDraft draft)
    {
        Override? intent = null;
        Configuration.TryUse(draft, () =>
        {
            var accepted = Configuration.ReadAcceptedModOrder(draft)!;
            var local = ReadLocalOrder();
            bool warning = local is null || !accepted.Select(Normalize).SequenceEqual(local, StringComparer.Ordinal);
            if (Configuration.IsCurrent(draft)) intent = new(this, draft, warning, local);
        }, requireIdle: true);
        return intent;
    }
    internal Task? UpdateWorldAsync(ClientAdminConfigurationDraft draft)
    {
        Task? work = null;
        Configuration.TryUse(draft, () =>
        {
            var captured = appearance();
            Configuration.TryUse(draft, () => work = world.StartAsync(ClientWorldWriteKind.Baseline, captured), requireIdle: true);
        }, requireIdle: true);
        return work;
    }
    private string[]? ReadLocalOrder() => source.ReadPackageOrder()?.Select(Normalize).ToArray();
    private string Normalize(string? id)
    {
        string value = (id ?? "").Trim(), postfix = source.SteamPostfix;
        if (postfix.Length > 0 && value.EndsWith(postfix, StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - postfix.Length);
        return value.ToLowerInvariant();
    }

    internal sealed class Override
    {
        private readonly ClientAdminManifestPanel panel;
        private readonly ClientAdminConfigurationDraft draft;
        private readonly string[]? localOrder;
        private bool used;
        internal bool RequiresWarning { get; }
        internal bool CanStart => !used && panel.Configuration.IsCurrent(draft) && !panel.Configuration.Busy;
        internal Override(ClientAdminManifestPanel panel, ClientAdminConfigurationDraft draft, bool warning, string[]? localOrder)
        { this.panel = panel; this.draft = draft; RequiresWarning = warning; this.localOrder = localOrder; }
        internal Task? StartAsync(Action<string, float>? progress, Action<string, bool> completion)
        {
            Task? work = null;
            panel.Configuration.TryUse(draft, () =>
            {
                if (used) return;
                // A warning for one local mod order must not authorize a different order.
                var current = panel.ReadLocalOrder();
                if (localOrder is not null && (current is null || !localOrder.SequenceEqual(current, StringComparer.Ordinal))) return;
                panel.Configuration.TryUse(draft, () =>
                {
                    if (used) return;
                    used = true;
                    work = panel.compatibility.StartAsync(progress, completion);
                }, requireIdle: true);
            }, requireIdle: true);
            return work;
        }
    }
}
