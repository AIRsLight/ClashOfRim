using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

// Ideology setup outlives the pending host publication, but belongs to that World.
// Reuse session/setup ownership rather than persisting another multiplayer flag.
internal sealed class ClientSharedIdeologyWorld
{
    internal static ClientSharedIdeologyWorld? Current { get; private set; }
    internal static void Attach(ClientSharedIdeologyWorld? value) => Current = value;
    private readonly ClientWorldSetupState setup;
    private readonly ClientSessionLifecycle session;
    private System.WeakReference<World>? setupWorld;

    internal ClientSharedIdeologyWorld(ClientWorldSetupState setup, ClientSessionLifecycle session)
    { this.setup = setup; this.session = session; }

    internal static bool IsSharedWorld => Current?.IsShared ?? HasServerIdeologies;
    private bool IsShared => (setupWorld?.TryGetTarget(out var world) == true && ReferenceEquals(world, Find.World))
        || HasServerIdeologies || setup.Pending || ClientMultiplayerWorldStatus.IsActive(session);
    private static bool HasServerIdeologies => Find.FactionManager?.AllFactions.Any(faction =>
        faction.ideos?.AllIdeos.Any(PlayerIdeoForkPolicy.HasServerGlobalKey) == true) == true;

    internal void StartingSiteOpened()
    {
        // Capture before the host submission consumes Pending. Normalization still
        // uses the existing package roundtrip and preserves native Ideo references.
        if (!setup.Pending || Find.World is not { } world) return;
        setupWorld = new(world);
        RemoteIdeoMaterializer.NormalizeGeneratedIdeologies();
    }
}
