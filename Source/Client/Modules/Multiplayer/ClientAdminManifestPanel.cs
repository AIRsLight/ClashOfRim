using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

// Configuration edits and delayed operations share the editor's exact draft ownership.
// The existing domain modules still own admission, capture, transport and publication.
internal sealed class ClientAdminManifestPanel
{
    internal ClientAdminConfigurationEditor Configuration { get; }
    private readonly IClientCompatibilityModOrderSource source;
    private readonly ClientCompatibilityOverride compatibility;
    private readonly ClientWorldWrites world;
    private readonly Func<ModColonyAppearanceDto?> appearance;
    internal ClientAdminManifestPanel(ClientAdminConfigurationEditor configuration, IClientCompatibilityModOrderSource source,
        ClientCompatibilityOverride compatibility, ClientWorldWrites world, Func<ModColonyAppearanceDto?> appearance)
    { Configuration = configuration; this.source = source; this.compatibility = compatibility; this.world = world; this.appearance = appearance; }

    internal ClientCompatibilityOverrideIntent? PrepareOverride(ClientAdminConfigurationDraft draft)
        => ClientCompatibilityOverrideIntent.Prepare(source, compatibility, Configuration.ReadAcceptedModOrder(draft),
            action => Configuration.TryUse(draft, action, requireIdle: true),
            () => Configuration.IsCurrent(draft) && !Configuration.Busy);
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
}
