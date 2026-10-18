using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal sealed class ClientBankState
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly IClientBankPresentation presentation;

    internal ClientBankState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientBankPresentation presentation)
    {
        this.session = session;
        this.game = game;
        this.presentation = presentation;
    }

    private ClientSessionLifecycle.Ownership? owner;
    private ClashOfRimClientNetworkContext? context;
    private IClientSnapshotGameSession? owningGame;
    private ClientSynchronizationCoordinator.Lease? work;
    private ModBankStatusResponseDto? view;
    private string? message;

    internal bool Busy { get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return work?.IsCurrent == true; } }
    internal string Message
    {
        get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return message ?? presentation.Text("StatusNotPulled"); }
        set { using var p = ClientSessionWork.EnterPublication(); Normalize(); message = value; }
    }
    internal ModBankStatusResponseDto? View
    {
        get { using var p = ClientSessionWork.EnterPublication(); Normalize(); return view is null ? null : ClientBankView.Copy(view); }
        set { using var p = ClientSessionWork.EnterPublication(); Normalize(); view = value is null ? null : ClientBankView.Copy(value); }
    }
    internal void Begin(ClientSnapshotTransaction.Attempt attempt)
    {
        using var p = ClientSessionWork.EnterPublication();
        Normalize();
        work = attempt.Admission;
    }
    internal void Reset()
    {
        using var p = ClientSessionWork.EnterPublication();
        owner = null;
        Normalize();
    }
    private void Normalize()
    {
        if (owner is not null && context is not null && owningGame?.IsCurrent == true && session.Owns(owner, context, false)) return;
        work?.Dispose();
        work = null;
        view = null;
        message = null;
        owner = session.CaptureOwnership();
        context = session.ReadContext();
        owningGame = game.BindCurrentGame();
    }
}
