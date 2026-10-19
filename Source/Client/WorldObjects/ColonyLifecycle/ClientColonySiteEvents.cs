using System;
using System.Threading.Tasks;
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
    private readonly ClientColonyAppearance appearance;
    internal ClientColonySiteEvents(ClientColonyRegistration registration, ClientColonyAppearance appearance)
    { this.registration = registration; this.appearance = appearance; }

    internal async Task NotifyAsync(MapParent? parent, Map? map, ClientColonySiteEvent kind)
    {
        ClientColonySiteChange? Capture()
        {
            if (map?.IsPlayerHome != true && (parent is null || parent.Faction != Faction.OfPlayer)) return null;
            if (ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility()) return null;
            string reason = ClashOfRimText.Key(kind == ClientColonySiteEvent.Settled
                ? "ClashOfRim.WorldCatalog.ReasonPlayerColonyMapCreated" : "ClashOfRim.WorldCatalog.ReasonPlayerColonyMapRemoved");
            var selected = appearance.Capture();
            return ReferenceEquals(Current, this) ? new(reason, selected) : null;
        }
        try { await registration.NotifySitesAsync(Capture).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning("[ClashOfRim] Failed to request player colony site " + kind + " registration: " + ex); }
    }
}
