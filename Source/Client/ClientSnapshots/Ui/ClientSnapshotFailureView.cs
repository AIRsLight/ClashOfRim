using System;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotFailureView : IClientSnapshotFailureView
{
    private SnapshotConfirmationFailureWindow? window;
    private ClientLocalMutationCoordinator.Failure? owner;

    public void Enqueue(Action action) => ClientMainThreadDispatcher.Enqueue(action);

    public void Show(ClientLocalMutationCoordinator.Failure failure, Action? retry, Action exit, Func<bool> belongsToCurrentSession, Action closed)
    {
        Find.WindowStack.WindowOfType<SnapshotConfirmationFailureWindow>()?.Close();
        owner = failure;
        window = new SnapshotConfirmationFailureWindow(ClientLocalMutationPresentation.OperationName(failure.State.Kind),
            failure.State.Status, retry, exit, belongsToCurrentSession);
        window.Closed += () =>
        {
            if (ReferenceEquals(owner, failure)) { window = null; owner = null; }
            closed();
        };
        Find.WindowStack.Add(window);
    }

    public void Close(ClientLocalMutationCoordinator.Failure failure)
    {
        // A queued close from an accepted operation must not hide a later failure.
        if (!ReferenceEquals(owner, failure)) return;
        window?.Close();
        window = null;
        owner = null;
    }

    public void ReturnToMenu()
    {
        Messages.Message(ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.ReturningToMainMenu"),
            MessageTypeDefOf.NeutralEvent, historical: false);
        GenScene.GoToMainMenu();
    }
}
