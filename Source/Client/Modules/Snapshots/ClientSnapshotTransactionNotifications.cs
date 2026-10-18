using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal interface IClientSnapshotTransactionPresentation
{
    void Log(SnapshotTransactionNotice notice);
    string Uploading { get; }
    bool Playing { get; }
    void Enqueue(Action action, Action discarded);
    void Message(string message);
}

// Diagnostic records describe what occurred; a queued "uploading" message only
// belongs to the still-running transaction and its original identity/Game.
internal sealed class ClientSnapshotTransactionNotifications
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientSnapshotGame game;
    private readonly IClientSnapshotTransactionPresentation view;
    internal ClientSnapshotTransactionNotifications(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        IClientSnapshotGame game, IClientSnapshotTransactionPresentation view)
    { this.session = session; this.synchronization = synchronization; this.game = game; this.view = view; }

    internal void Observe(SnapshotTransactionNotice notice)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (notice.Phase != SnapshotTransactionPhase.Started) { view.Log(notice); return; }
        var owner = session.CaptureOwnership(); var context = session.ReadContext(); var source = game.BindCurrentGame();
        bool Current() => synchronization.IsCurrent(notice) && source.IsCurrent && session.Owns(owner, context, false);
        view.Log(notice);
        if (!Current()) return;
        string message = view.Uploading;
        if (string.IsNullOrWhiteSpace(message) || !Current()) return;
        view.Enqueue(() =>
        {
            using var publication = ClientSessionWork.EnterPublication();
            if (Current() && view.Playing) view.Message(message);
        }, static () => { });
    }
}
