using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Synchronization;

// Local changes remain blocked after upload admission is released. This state is
// session-owned and independent of UI language; it does not acquire a second upload gate.
internal sealed class ClientLocalMutationCoordinator
{
    private readonly object gate = new();
    private LocalMutationState? current;
    private Failure? failure;
    private TaskCompletionSource changed = NewChangeSignal();

    private static TaskCompletionSource NewChangeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SignalChanged()
    { var previous = changed; changed = NewChangeSignal(); previous.TrySetResult(); }

    // Queued business work must wait for an unresolved local mutation without
    // holding snapshot admission or polling the failure window.
    internal async Task WaitUntilClearAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate) { token.ThrowIfCancellationRequested(); if (current is null) return; wait = changed.Task; }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal LocalMutationState? Current { get { lock (gate) return current; } }
    internal bool IsPending { get { lock (gate) return current is not null; } }

    internal void Begin(LocalMutationKind kind, string status)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            failure = null;
            current = new LocalMutationState(kind, status, ClientSessionWork.Token);
            SignalChanged();
        }
    }

    internal void UpdateStatus(string status)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) if (current is not null) current = current with { Status = status };
    }

    internal void Reset()
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) { current = null; failure = null; SignalChanged(); }
    }

    internal LocalMutationState? BlockingMutation(LocalMutationKind? continuation = null)
    {
        lock (gate) return current is not null && current.Kind != continuation ? current : null;
    }

    internal LocalMutationAdmission CheckAdmission(bool configured, bool hasSnapshot, LocalMutationKind? continuation = null)
    {
        lock (gate)
        {
            if (!configured) return LocalMutationAdmission.NotConfigured;
            if (current is not null && current.Kind != continuation) return LocalMutationAdmission.PendingMutation;
            return hasSnapshot ? LocalMutationAdmission.Allowed : LocalMutationAdmission.SnapshotMissing;
        }
    }

    internal Failure RecordFailure(LocalMutationKind kind, string message, Action retry, bool canRetry)
    {
        if (retry is null) throw new ArgumentNullException(nameof(retry));
        using var publication = ClientSessionWork.EnterPublication();
        Begin(kind, message);
        lock (gate) return failure = new Failure(current!, retry, canRetry);
    }

    internal bool IsCurrent(Failure candidate)
    {
        lock (gate) return ClientSessionWork.IsCurrent && ReferenceEquals(failure, candidate)
            && candidate.State.Generation == ClientSessionWork.Token;
    }

    internal bool TryRetry(Failure candidate) => ResolveFailure(candidate, retry: true, exit: null);
    internal bool TryExit(Failure candidate, Action exit) => ResolveFailure(candidate, retry: false, exit);

    private bool ResolveFailure(Failure candidate, bool retry, Action? exit)
    {
        bool accepted = false;
        ClientSessionWork.TryApply(() =>
        {
            lock (gate)
            {
                if (!IsCurrent(candidate) || (retry && !candidate.CanRetry)) return;
                failure = null;
                if (!retry) { current = null; SignalChanged(); }
                accepted = true;
            }
            // Consume the decision before invoking it: old windows cannot retry twice
            // or discard a later failure, including another failure of the same kind.
            if (retry) candidate.Retry(); else exit!();
        });
        return accepted;
    }

    internal sealed record Failure(LocalMutationState State, Action Retry, bool CanRetry);
}

internal sealed record LocalMutationState(LocalMutationKind Kind, string Status, CancellationToken Generation);
internal enum LocalMutationAdmission { Allowed, NotConfigured, PendingMutation, SnapshotMissing }
internal enum LocalMutationKind
{
    None, Bank, Mercenary, AutomaticEvents, Gift, Support, Raid, RaidRestore,
    RaidAttacker, Shop, Trade, ColonyRelocation, GiftSend, SupportSend, Endgame, SupportArrival, SnapshotRestore
}
