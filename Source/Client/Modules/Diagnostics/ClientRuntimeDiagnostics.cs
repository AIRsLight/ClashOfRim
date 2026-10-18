using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Diagnostics;

// Latest presentation messages retain their publishers' lifetime. Reading this
// projection does not start work, reset domains or duplicate event status storage.
internal sealed class ClientRuntimeDiagnostics
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientEventState events;
    internal ClientRuntimeDiagnostics(ClientSessionLifecycle session, ClientEventState events)
    { this.session = session; this.events = events; }

    internal string? LoginStatus { get; set; }
    internal string? SnapshotUploadStatus { get; set; }
    internal string? PresenceStatus { get; set; }
    internal string? WorldMapStatus { get; set; }

    internal ClientDiagnosticsView Read()
    {
        using var publication = ClientSessionWork.EnterPublication();
        var context = session.ReadContext();
        return new(context.ServerBaseUrl, context.UserId, context.ColonyId, context.CurrentSnapshotId, context.IsConfigured,
            LoginStatus, SnapshotUploadStatus, PresenceStatus, WorldMapStatus, events.QueueStatus, events.DetailsStatus);
    }
}

// Whitelist display data; authenticated context and credentials never reach the page.
internal sealed record ClientDiagnosticsView(string ServerBaseUrl, string UserId, string ColonyId, string? SnapshotId, bool IsConfigured,
    string? LoginStatus, string? SnapshotUploadStatus, string? PresenceStatus, string? WorldMapStatus, string QueueStatus, string DetailsStatus);
