using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal sealed class ClientBankState
{
    private readonly ClientGameRequestTracker lifetime;
    private readonly IClientBankPresentation presentation;
    private ClientSynchronizationCoordinator.Lease? work;
    private ModBankStatusResponseDto? view;
    private string? message;
    private int version;

    internal ClientBankState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientBankPresentation presentation)
    {
        this.presentation = presentation;
        lifetime = new(session, game, () =>
        { work?.Dispose(); work = null; view = null; message = null; version++; });
    }

    // Bank operations release the exact transaction lease. Keep busy tied to
    // that lease, rather than adding a second request completion mechanism.
    internal bool Busy => lifetime.Read(() => work?.IsCurrent == true);
    internal int Version => lifetime.Read(() => version);
    internal string Message
    {
        get => lifetime.Read(() => message ?? presentation.Text("StatusNotPulled"));
        set => lifetime.Read(() => message = value);
    }
    internal ModBankStatusResponseDto? View
    {
        get => lifetime.Read(() => view is null ? null : ClientBankView.Copy(view));
        set => lifetime.Read(() => { view = value is null ? null : ClientBankView.Copy(value); return ++version; });
    }
    internal void Begin(ClientSnapshotTransaction.Attempt attempt) => lifetime.Read(() => work = attempt.Admission);
    internal void Reset() => lifetime.Reset();
    internal Action Bind(Action action)
    {
        var owner = lifetime.Capture();
        return () => lifetime.TryApply(owner, action);
    }
}
