using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Synchronization;

// All manual/snapshot admission shares this state. Lock order remains session publication,
// then synchronization state. A lease releases only the operation that acquired it.
internal sealed class ClientSynchronizationCoordinator
{
    private readonly object gate = new();
    private readonly Func<DateTime> utcNow;
    private Lease? manual;
    private Lease? snapshot;
    private long sequence;

    internal ClientSynchronizationCoordinator(Func<DateTime>? utcNow = null) => this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    internal bool ManualInProgress { get { lock (gate) return manual is not null; } }
    internal bool SnapshotInProgress { get { lock (gate) return snapshot is not null; } }
    internal bool IsBusy { get { lock (gate) return manual is not null || snapshot is not null; } }
    internal event Action<SnapshotTransactionNotice>? SnapshotChanged;

    internal bool TryBeginManual([NotNullWhen(true)] out Lease? lease, Lease? continuation = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            lease = null;
            if (continuation is not null)
            {
                if (!ReferenceEquals(manual, continuation) || continuation.IsSnapshot
                    || continuation.Generation != ClientSessionWork.Token) return false;
                lease = continuation;
                return true;
            }
            if (manual is not null || snapshot is not null) return false;
            manual = lease = new Lease(this, false, string.Empty, utcNow(), ClientSessionWork.Token);
            return true;
        }
    }

    internal bool TryAcquireSnapshot([NotNullWhen(true)] out Lease? lease, Lease? continuation = null,
        bool allowExistingManualSync = false, [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
    {
        using var publication = ClientSessionWork.EnterPublication();
        string caller = FormatCaller(callerMember, callerFile, callerLine);
        SnapshotTransactionNotice notice;
        lock (gate)
        {
            lease = null;
            if (continuation is not null && ReferenceEquals(snapshot, continuation) && continuation.Generation == ClientSessionWork.Token)
            {
                lease = continuation;
                return true;
            }
            bool promotingManual = continuation is not null && ReferenceEquals(manual, continuation)
                && !continuation.IsSnapshot && continuation.Generation == ClientSessionWork.Token;
            if ((continuation is not null && !promotingManual) || snapshot is not null
                || (manual is not null && !promotingManual && (!allowExistingManualSync || !manual.IsLegacyManual)))
                notice = Describe(SnapshotTransactionPhase.Rejected, caller, allowExistingManualSync);
            else
            {
                snapshot = manual = lease = new Lease(this, true, caller, utcNow(), ClientSessionWork.Token);
                sequence++;
                notice = Describe(SnapshotTransactionPhase.Started, caller, allowExistingManualSync);
            }
        }
        SnapshotChanged?.Invoke(notice);
        return lease is not null;
    }

    // Transitional write port for pre-existing manual flows. M3's next step replaces these
    // callers with explicit leases. It cannot clear an owned snapshot or replace its lease.
    internal void SetLegacyManualInProgress(bool value)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            if (value) manual ??= snapshot ?? new Lease(this, false, string.Empty, utcNow(), ClientSessionWork.Token, legacyManual: true);
            else if (snapshot is null && manual?.IsLegacyManual == true) manual = null;
        }
    }

    // Explicit session exit / authoritative reload, never a normal request finalizer.
    internal void Reset()
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) { manual = null; snapshot = null; }
    }

    private bool Owns(Lease lease)
    {
        lock (gate) return lease.Generation == ClientSessionWork.Token && ReferenceEquals(manual, lease);
    }

    private Lease TransferManual(Lease lease)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!Owns(lease) || lease.IsSnapshot)
            throw new InvalidOperationException("Only the current manual operation can transfer its admission.");
        lock (gate)
        {
            return manual = new Lease(this, false, lease.Caller, lease.StartedAtUtc, lease.Generation);
        }
    }

    private void Release(Lease lease, Action? completed = null)
    {
        ClientSessionWork.TryApply(() =>
        {
            if (!Owns(lease)) return;
            SnapshotTransactionNotice? notice = null;
            try { completed?.Invoke(); }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(snapshot, lease))
                    {
                        notice = Describe(SnapshotTransactionPhase.Ended, lease.Caller, false);
                        snapshot = null;
                    }
                    if (ReferenceEquals(manual, lease)) manual = null;
                }
                if (notice is not null) SnapshotChanged?.Invoke(notice);
            }
        });
    }

    private SnapshotTransactionNotice Describe(SnapshotTransactionPhase phase, string caller, bool allowManual) =>
        new(phase, caller, allowManual, snapshot?.Caller ?? string.Empty, sequence, snapshot is not null, manual is not null,
            snapshot is null ? null : utcNow() - snapshot.StartedAtUtc);

    private static string FormatCaller(string member, string file, int line)
    {
        string name = string.IsNullOrWhiteSpace(file) ? "<unknown>" : Path.GetFileName(file);
        return (string.IsNullOrWhiteSpace(member) ? name : member + "@" + name) + ":" + line.ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class Lease : IDisposable
    {
        private readonly ClientSynchronizationCoordinator owner;
        internal Lease(ClientSynchronizationCoordinator owner, bool snapshot, string caller, DateTime startedAtUtc, CancellationToken generation, bool legacyManual = false)
        { this.owner = owner; IsSnapshot = snapshot; IsLegacyManual = legacyManual; Caller = caller; StartedAtUtc = startedAtUtc; Generation = generation; }
        internal bool IsSnapshot { get; }
        internal bool IsLegacyManual { get; }
        internal string Caller { get; }
        internal DateTime StartedAtUtc { get; }
        internal CancellationToken Generation { get; }
        public void Dispose() => owner.Release(this);
        internal void Complete(Action completed) => owner.Release(this, completed);

        // Async stages own a new lease before the caller returns. Their finally cannot
        // release a later UI/async stage or a promoted snapshot.
        internal Task RunContinuation(Func<Lease, Task> action)
        {
            Lease next = owner.TransferManual(this);
            try
            {
                return ClientSessionWork.Run(async () =>
                {
                    try
                    {
                        Task? work = null;
                        ClientSessionWork.TryApply(() =>
                        {
                            if (owner.Owns(next)) work = action(next);
                        });
                        if (work is not null) await work;
                    }
                    finally { next.Dispose(); }
                });
            }
            catch { next.Dispose(); throw; }
        }

        // Transfer before enqueueing: the producer's finally can no longer release the
        // queued operation. A promoted snapshot owns its own completion independently.
        internal void QueueContinuation(Action<Action, Action> enqueue, Action<Lease> action, Action? completed = null)
        {
            Lease next = owner.TransferManual(this);
            void Finish() => owner.Release(next, completed);
            try
            {
                enqueue(() =>
                {
                    try
                    {
                        ClientSessionWork.TryApply(() =>
                        {
                            if (owner.Owns(next)) action(next);
                        });
                    }
                    finally { Finish(); }
                }, Finish);
            }
            catch { Finish(); throw; }
        }
    }
}

internal enum SnapshotTransactionPhase { Started, Rejected, Ended }
internal sealed record SnapshotTransactionNotice(SnapshotTransactionPhase Phase, string RequestedOwner, bool AllowExistingManualSync,
    string Owner, long Sequence, bool SnapshotInProgress, bool ManualInProgress, TimeSpan? Age);
