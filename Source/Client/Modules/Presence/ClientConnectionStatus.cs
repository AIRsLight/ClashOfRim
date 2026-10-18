using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Presence;

internal interface IClientConnectionStatusPresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    void Status(string text);
    void Diagnose(string text, bool warning);
}

// Status and diagnostics describe the source connection, even if localization or
// a presentation callback changes the selected account or opens another stream.
internal sealed class ClientConnectionStatus
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientConnectionStatusPresentation view;
    internal ClientConnectionStatus(ClientSessionLifecycle session, ClientSessionConnection connection, IClientConnectionStatusPresentation view)
    {
        this.session = session; this.view = view;
        connection.StatusChanged += Present;
        session.ExitCompleted += PresentExit;
    }
    private void PresentExit(ClientSessionLifecycle.Exit exit)
    {
        string text = "";
        if (session.ContinueExit(exit, () => text = view.Text("ClashOfRim.Presence.StatusSessionDisconnected")))
            session.ContinueExit(exit, () => view.Status(text));
    }
    private void Present(SessionConnectionStatus status, ClientSessionConnection.Publication source)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = session.CaptureOwnership();
        var context = status.Context ?? session.ReadContext();
        bool Apply(Action action)
        {
            bool applied = false;
            source.TryApply(() =>
            {
                if (!session.Owns(owner, context, false)) return;
                action(); applied = true;
            });
            return applied;
        }
        bool Step(Action action) => Apply(action) && Apply(static () => { });
        string? suffix = status.Phase switch
        {
            SessionConnectionPhase.NotConfigured => "StatusNotConfigured",
            SessionConnectionPhase.SessionMissing => "StatusSessionMissing",
            SessionConnectionPhase.Connecting => "StatusConnecting",
            SessionConnectionPhase.Disconnecting => "StatusDisconnecting",
            SessionConnectionPhase.Cancelled => "StatusDisconnectedManual",
            SessionConnectionPhase.Closed => "StatusClosed",
            SessionConnectionPhase.Failed => "StatusFailed",
            SessionConnectionPhase.Exception => "StatusException",
            _ => null
        };
        if (suffix is null) return;
        (string Name, object? Value)[] args = status.Phase switch
        {
            SessionConnectionPhase.Failed => new[] { ("CODE", (object?)(status.ErrorCode ?? "")), ("MESSAGE", (object?)(status.Message ?? "")) },
            SessionConnectionPhase.Exception => new[] { ("TYPE", (object?)status.Error!.GetType().Name), ("MESSAGE", (object?)status.Error.Message) },
            _ => Array.Empty<(string, object?)>()
        };
        string text = "";
        if (!Step(() => text = view.Text("ClashOfRim.Presence." + suffix, args)) || !Step(() => view.Status(text))) return;
        switch (status.Phase)
        {
            case SessionConnectionPhase.SessionMissing:
                Step(() => view.Diagnose("Cannot start session stream without session id.", warning: true)); break;
            case SessionConnectionPhase.Connecting:
                Step(() => view.Diagnose("Starting WebSocket session stream: user=" + context.UserId
                    + ", colony=" + context.ColonyId + ", session=" + status.SessionId, warning: false)); break;
            case SessionConnectionPhase.Exception:
                Step(() => view.Diagnose("Session stream failed: " + status.Error, warning: true)); break;
        }
    }
}
