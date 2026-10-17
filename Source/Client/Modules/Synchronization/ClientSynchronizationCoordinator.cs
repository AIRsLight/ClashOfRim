using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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

    internal bool TryBeginManual([NotNullWhen(true)] out Lease? lease)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            lease = null;
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

    private void Release(Lease lease)
    {
        ClientSessionWork.TryApply(() =>
        {
            if (lease.Generation != ClientSessionWork.Token) return;
            SnapshotTransactionNotice? notice = null;
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
    }
}

internal enum SnapshotTransactionPhase { Started, Rejected, Ended }
internal sealed record SnapshotTransactionNotice(SnapshotTransactionPhase Phase, string RequestedOwner, bool AllowExistingManualSync,
    string Owner, long Sequence, bool SnapshotInProgress, bool ManualInProgress, TimeSpan? Age);
