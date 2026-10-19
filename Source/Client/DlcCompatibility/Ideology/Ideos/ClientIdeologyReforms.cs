using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal sealed class ClientIdeologyReforms
{
    internal static ClientIdeologyReforms? Current { get; private set; }
    internal static void Attach(ClientIdeologyReforms? value) => Current = value;
    private readonly ClientColonyRegistration registration;
    internal ClientIdeologyReforms(ClientColonyRegistration registration) => this.registration = registration;

    internal async Task NotifyAsync(Ideo? ideo)
    {
        if (!ReferenceEquals(Current, this)) return;
        ClientColonyRegistrationChange? Capture()
        {
            if (ideo is null || !IdeologyPawnReferenceCompatibility.HasPawnReference || RemoteIdeoCatalog.IsRemoteShadow(ideo)
                || !(ideo.initialPlayerIdeo || Faction.OfPlayer?.ideos?.Has(ideo) == true)) return null;
            string reason = ClashOfRimText.Key("ClashOfRim.WorldCatalog.ReasonIdeoReformed");
            return ReferenceEquals(Current, this) ? new(reason) : null;
        }
        try { await registration.NotifyAsync(ClientColonyRegistrationKind.Extensions, Capture).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning("[ClashOfRim][Ideo] Failed to request world extension sync after reform: " + ex); }
    }
}
