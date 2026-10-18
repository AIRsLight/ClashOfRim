using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Multiplayer.Diagnostics;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerDiagnosticsPage
{
    private readonly ClientRuntimeDiagnostics diagnostics;
    internal MultiplayerDiagnosticsPage(ClientRuntimeDiagnostics diagnostics) => this.diagnostics = diagnostics;

    internal void Draw(Rect rect)
    {
        var view = diagnostics.Read();
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Listing_Standard listing = new();
        listing.Begin(inner);
        listing.Label($"{view.UserId}/{view.ColonyId}");
        listing.GapLine();
        listing.Label(ClientDiagnosticsText.Format(view));
        listing.Label(ClientDiagnosticsText.SnapshotUpload(view.SnapshotUploadStatus));
        listing.Label(ClientDiagnosticsText.Login(view.LoginStatus));
        listing.Label(ClientDiagnosticsText.WorldMap(view.WorldMapStatus));
        listing.Label(ClientDiagnosticsText.Presence(view.PresenceStatus));
        listing.GapLine();
        listing.Label(view.QueueStatus);
        listing.Label(view.DetailsStatus);
        listing.End();
    }
}
