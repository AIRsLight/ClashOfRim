using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Status and stream cursor share the reader's existing identity/Game ownership.
// This state can be wired to confirmation/application presentation before refresh
// scheduling is composed; those modules do not depend on a partially built host.
internal sealed class ClientEventState
{
    private readonly ClientEventReader reader;
    private readonly IClientEventReadPresentation presentation;
    private ClientEventReader.ReadScope? stateOwner;
    private string? queueStatus, detailsStatus, actionStatus;
    private long notificationVersion;

    internal ClientEventState(ClientEventReader reader, IClientEventReadPresentation presentation)
    { this.reader = reader; this.presentation = presentation; }

    internal string QueueStatus { get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return queueStatus ?? presentation.Format(new(ClientEventRefreshStage.InitialQueue)); } }
    internal string DetailsStatus { get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return detailsStatus ?? presentation.Format(new(ClientEventRefreshStage.InitialDetails)); } }
    internal long NotificationVersion { get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return notificationVersion; } }
    internal string ActionStatus
    {
        get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return actionStatus ?? presentation.Format(new(ClientEventRefreshStage.InitialAction)); }
        set { using var p = ClientSessionWork.EnterPublication(); Normalize(); actionStatus = value; }
    }
    internal void SetQueueStatus(string message) { using var p = ClientSessionWork.EnterPublication(); Normalize(); queueStatus = message; }
    internal void SetDetailsStatus(string message) { using var p = ClientSessionWork.EnterPublication(); Normalize(); detailsStatus = message; }
    internal void ObserveNotificationVersion(long version)
    { using var p = ClientSessionWork.EnterPublication(); Normalize(); notificationVersion = Math.Max(notificationVersion, version); }

    internal void Reset()
    {
        using var publication = ClientSessionWork.EnterPublication();
        stateOwner = null;
        Normalize();
    }

    private void Normalize()
    {
        if (stateOwner is not null && reader.Owns(stateOwner)) return;
        stateOwner = reader.CaptureScope();
        notificationVersion = 0;
        queueStatus = detailsStatus = actionStatus = null;
    }

    internal void Publish(ClientEventRefreshNotice notice)
    {
        using var publication = ClientSessionWork.EnterPublication();
        Normalize();
        string text = presentation.Format(notice);
        bool details = notice.Kind == ClientEventReadKind.Details;
        if (notice.Update is { } update && update.Failure?.Kind != ClientEventReadFailureKind.Exception)
            details = update.Stage == ClientEventReadStage.Details;
        if (details) detailsStatus = text; else queueStatus = text;
        presentation.Diagnose(notice);
    }
}
