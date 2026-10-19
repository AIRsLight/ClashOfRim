using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal enum ClientColonySiteEvent { Settled, Removed }
internal sealed class ClientColonySiteEvents
{
    internal static ClientColonySiteEvents? Current { get; private set; }
    internal static void Attach(ClientColonySiteEvents? value) => Current = value;
    private readonly ClientColonyRegistration registration;
    private readonly Func<ModColonyAppearanceDto?> captureAppearance;
    internal ClientColonySiteEvents(ClientColonyRegistration registration, ClientColonyAppearance appearance)
    { this.registration = registration; captureAppearance = appearance.Capture; }

    internal Task PollAsync(Game game, int ticks)
        => ReferenceEquals(Current, this) && ReferenceEquals(game, Verse.Current.Game)
            ? registration.PollSitesAsync(ticks, captureAppearance) : Task.CompletedTask;

    internal async Task NotifyAsync(MapParent? parent, Map? map, ClientColonySiteEvent kind)
    {
        ClientColonyRegistrationChange? Capture()
        {
            if (map?.IsPlayerHome != true && (parent is null || parent.Faction != Faction.OfPlayer)) return null;
            if (ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility()) return null;
            string reason = ClashOfRimText.Key(kind == ClientColonySiteEvent.Settled
                ? "ClashOfRim.WorldCatalog.ReasonPlayerColonyMapCreated" : "ClashOfRim.WorldCatalog.ReasonPlayerColonyMapRemoved");
            var selected = captureAppearance();
            return ReferenceEquals(Current, this) ? new(reason, selected) : null;
        }
        try { await registration.NotifyAsync(ClientColonyRegistrationKind.Sites, Capture).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning("[ClashOfRim] Failed to request player colony site " + kind + " registration: " + ex); }
    }
}
