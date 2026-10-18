using AIRsLight.ClashOfRim.Modules.Diagnostics;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Diagnostics;

internal static class ClientDiagnosticsText
{
    internal static string Login(string? message) => message ?? ClashOfRimText.Key("ClashOfRim.Status.NotLoggedIn");
    internal static string SnapshotUpload(string? message) => message ?? ClashOfRimText.Key("ClashOfRim.Status.SnapshotUploadNotStarted");
    internal static string Presence(string? message) => message ?? ClashOfRimText.Key("ClashOfRim.Status.PresenceNotStarted");
    internal static string WorldMap(string? message) => message ?? ClashOfRimText.Key("ClashOfRim.Status.WorldMapNotSynced");

    internal static string Format(ClientDiagnosticsView view) => Describe(Find.CurrentMap?.uniqueID, view);
    internal static string Describe(int? mapId, ClientDiagnosticsView view)
    {
        string map = mapId is { } id ? ClashOfRimText.Key("ClashOfRim.CurrentMapSummary", id.Named("MAPID"))
            : ClashOfRimText.Key("ClashOfRim.CurrentMapMissing");
        string configuration = view.IsConfigured
            ? ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationSummary", view.ServerBaseUrl.Named("SERVER"), view.UserId.Named("USER"), view.ColonyId.Named("COLONY"))
            : ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationMissing");
        string snapshot = string.IsNullOrWhiteSpace(view.SnapshotId) ? ClashOfRimText.Key("ClashOfRim.CurrentSnapshotMissing")
            : ClashOfRimText.Key("ClashOfRim.CurrentSnapshotSummary", view.SnapshotId.Named("SNAPSHOT"));
        return $"{map} {configuration} {snapshot}";
    }
}
