using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class SynchronizationCoordinatorTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        ClientSessionWork.Reset();
        try
        {
            AdmissionAndPromotion();
            ContinuationAndDiagnostics();
            LateCompletionAndReset();
            QueuedManualOwnership();
            QueuedManualFailures();
            await AsyncManualHandoffs();
            await AsyncManualFailures();
            DeferredDecisionOwnership();
            await ExpiredCallbacks();
            await CompetingAdmissions();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void AdmissionAndPromotion()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(!sync.IsBusy && !sync.ManualInProgress && !sync.SnapshotInProgress, "New coordinator is busy.");
        Require(sync.TryBeginManual(out var manual), "Manual admission failed.");
        Require(sync.ManualInProgress && !sync.SnapshotInProgress && !sync.TryBeginManual(out _)
            && !sync.TryAcquireSnapshot(out _) && !sync.TryAcquireSnapshot(out _, allowExistingManualSync: true),
            "Manual admission was stolen by another operation or the legacy promotion port.");
        sync.SetLegacyManualInProgress(false);
        Require(sync.IsBusy, "Legacy finalizer cleared an owned manual operation.");
        Require(sync.TryAcquireSnapshot(out var promoted, manual), "Explicit manual promotion failed.");
        manual!.Dispose();
        sync.SetLegacyManualInProgress(false);
        Require(sync.ManualInProgress && sync.SnapshotInProgress && !sync.TryBeginManual(out _),
            "Promoted snapshot lost its admission when the old manual operation completed.");
        promoted!.Dispose();
        Require(!sync.IsBusy, "Promoted snapshot did not release both admissions.");

        sync.SetLegacyManualInProgress(true);
        Require(!sync.TryAcquireSnapshot(out _), "Snapshot implicitly consumed legacy manual work.");
        Require(sync.TryAcquireSnapshot(out var legacyPromotion, allowExistingManualSync: true), "Legacy promotion broke existing flows.");
        sync.SetLegacyManualInProgress(true);
        sync.SetLegacyManualInProgress(false);
        Require(sync.SnapshotInProgress && sync.ManualInProgress, "Legacy writes replaced a snapshot lease.");
        legacyPromotion!.Dispose();
        Require(!sync.IsBusy, "Legacy promotion retained stale manual state.");
        sync.SetLegacyManualInProgress(true);
        sync.SetLegacyManualInProgress(false);
        Require(!sync.IsBusy, "Unpromoted legacy flow cannot complete.");
    }

    private static void ContinuationAndDiagnostics()
    {
        DateTime now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
        var sync = new ClientSynchronizationCoordinator(() => now);
        var notices = new List<SnapshotTransactionNotice>();
        sync.SnapshotChanged += notices.Add;
        Require(sync.TryAcquireSnapshot(out var first, callerMember: "Upload", callerFile: "Relocation.cs", callerLine: 42), "Snapshot rejected.");
        Require(sync.TryAcquireSnapshot(out var continued, first) && ReferenceEquals(first, continued) && notices.Count == 1,
            "Handoff created a second transaction or a duplicate start notification.");
        now = now.AddSeconds(3.25);
        Require(!sync.TryAcquireSnapshot(out _) && !sync.TryBeginManual(out _), "Concurrent operation was admitted.");
        var rejected = notices.Last();
        Require(rejected.Phase == SnapshotTransactionPhase.Rejected && rejected.Sequence == 1
            && rejected.Owner == "Upload@Relocation.cs:42" && rejected.Age == TimeSpan.FromSeconds(3.25)
            && rejected.ManualInProgress && rejected.SnapshotInProgress,
            "Busy diagnostics lost the actual owning transaction.");
        continued!.Dispose();
        first!.Dispose();
        Require(!sync.IsBusy && notices.Count(n => n.Phase == SnapshotTransactionPhase.Started) == 1
            && notices.Count(n => n.Phase == SnapshotTransactionPhase.Ended) == 1,
            "A handoff was released more than once.");
        Require(!sync.TryAcquireSnapshot(out _, first), "Disposed handoff was reused.");
        Require(sync.TryAcquireSnapshot(out var second), "Next snapshot was blocked.");
        Require(notices.Last().Sequence == 2, "Rejected admission or handoff advanced the transaction sequence.");
        first.Dispose();
        Require(sync.SnapshotInProgress, "Late duplicate completion released a new snapshot in the same session.");
        second!.Dispose();

        var other = new ClientSynchronizationCoordinator();
        Require(sync.TryAcquireSnapshot(out var owned), "Snapshot rejected.");
        Require(!other.TryAcquireSnapshot(out _, owned) && !other.IsBusy, "A foreign coordinator accepted a handoff.");
        owned!.Dispose();
    }

    private static void LateCompletionAndReset()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(sync.TryBeginManual(out var oldManual), "Manual rejected.");
        oldManual!.Dispose();
        Require(sync.TryBeginManual(out var newManual), "New manual rejected.");
        oldManual.Dispose();
        Require(sync.ManualInProgress, "Duplicate manual completion cleared its successor.");
        newManual!.Dispose();
        Require(sync.TryAcquireSnapshot(out var old), "Snapshot rejected.");
        sync.Reset(); // Authoritative reload can reset within the same session generation.
        Require(sync.TryAcquireSnapshot(out var replacement), "Reset did not free admission.");
        old!.Dispose();
        Require(!sync.TryAcquireSnapshot(out _, old) && sync.SnapshotInProgress,
            "Reset allowed a stale transaction to resume or release its successor.");
        replacement!.Dispose();
        Require(!sync.IsBusy, "Replacement cannot complete.");
    }

    private static async Task ExpiredCallbacks()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(sync.TryAcquireSnapshot(out var old), "Snapshot rejected.");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool admissionCancelled = false;
        Task stale = ClientSessionWork.Run(async () =>
        {
            started.SetResult(true);
            await resume.Task;
            old!.Dispose();
            try { sync.TryBeginManual(out _); }
            catch (OperationCanceledException) { admissionCancelled = true; }
        });
        bool ran = false;
        Action queued = ClientSessionWork.Guard(() => ran = true, old!.Dispose);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClientSessionWork.Reset();
        sync.Reset(); // The session lifecycle invokes this reset before new work is admitted.
        Require(sync.TryAcquireSnapshot(out var replacement), "New generation cannot acquire snapshot.");
        resume.SetResult(true);
        await stale.WaitAsync(TimeSpan.FromSeconds(5));
        queued();
        old.Dispose(); // Late callback with no old ambient token must also be harmless.
        Require(admissionCancelled && !ran && sync.SnapshotInProgress
            && !sync.TryAcquireSnapshot(out _, old), "An expired callback affected current admission.");
        replacement!.Dispose();
        Require(!sync.IsBusy, "New generation cannot release snapshot.");
    }

    private static async Task CompetingAdmissions()
    {
        for (int round = 0; round < 32; round++)
        {
            var sync = new ClientSynchronizationCoordinator();
            var accepted = new ConcurrentBag<ClientSynchronizationCoordinator.Lease>();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            {
                bool success = i % 2 == 0 ? sync.TryBeginManual(out var lease) : sync.TryAcquireSnapshot(out lease);
                if (success) accepted.Add(lease!);
            })));
            Require(accepted.Count == 1 && sync.IsBusy, "Competing manual and snapshot requests acquired multiple owners.");
            accepted.Single().Dispose();
            Require(!sync.IsBusy, "Concurrent admission could not be released.");
        }
    }

    private static void QueuedManualOwnership()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(sync.TryBeginManual(out var producer), "Producer was not admitted.");
        Action run = null!, discard = null!;
        int applied = 0, completed = 0;
        ClientSynchronizationCoordinator.Lease? snapshot = null;
        producer!.QueueContinuation((action, drop) => { run = action; discard = drop; }, continuation =>
        {
            applied++;
            Require(sync.TryBeginManual(out var same, continuation) && ReferenceEquals(same, continuation),
                "A synchronous preparation stage lost its manual ownership.");
            Require(sync.TryAcquireSnapshot(out snapshot, continuation), "Queued preparation could not promote its own admission.");
        }, () => completed++);
        producer.Complete(() => completed++);
        Require(sync.ManualInProgress && !sync.TryBeginManual(out _) && !sync.TryBeginManual(out _, producer)
            && !sync.TryAcquireSnapshot(out _, producer), "The producer released or reused transferred admission.");
        run();
        run();
        discard();
        Require(applied == 1 && completed == 0 && sync.SnapshotInProgress,
            "Queued work repeated or its finalizer cleared a promoted snapshot.");
        snapshot!.Complete(() => completed++);
        snapshot.Complete(() => completed++);
        Require(!sync.IsBusy && completed == 1, "Snapshot completion did not run exactly once.");

        Require(sync.TryBeginManual(out var first), "Producer rejected.");
        Action second = null!;
        first!.QueueContinuation((action, _) => run = action, next =>
            next.QueueContinuation((action, _) => second = action, _ => applied++, () => completed++), () => completed++);
        first.Dispose();
        run();
        Require(sync.ManualInProgress && applied == 1 && completed == 1, "Second handoff was released by the first queued stage.");
        second();
        Require(!sync.IsBusy && applied == 2 && completed == 2, "Chained continuation did not complete once.");
    }

    private static void QueuedManualFailures()
    {
        foreach (string outcome in new[] { "success", "discard", "enqueue-throw", "action-throw", "completion-throw", "reset", "new-session" })
        {
            ClientSessionWork.Reset();
            var sync = new ClientSynchronizationCoordinator();
            Require(sync.TryBeginManual(out var producer), "Producer rejected.");
            Action run = null!, discard = null!;
            int actions = 0, completions = 0;
            try
            {
                producer!.QueueContinuation((action, drop) =>
                {
                    run = action; discard = drop;
                    if (outcome == "enqueue-throw") throw new ApplicationException("queue failed");
                }, _ =>
                {
                    actions++;
                    if (outcome == "action-throw") throw new ApplicationException("apply failed");
                }, () =>
                {
                    completions++;
                    if (outcome == "completion-throw") throw new ApplicationException("completion failed");
                });
            }
            catch (ApplicationException) when (outcome == "enqueue-throw") { }
            producer!.Dispose();
            bool replaced = outcome is "reset" or "new-session";
            ClientSynchronizationCoordinator.Lease? newer = null;
            if (replaced)
            {
                if (outcome == "new-session") ClientSessionWork.Reset();
                sync.Reset();
                Require(sync.TryBeginManual(out newer), "Reset failed to admit new work.");
            }
            try { if (outcome == "discard") discard(); else run(); }
            catch (ApplicationException) when (outcome is "action-throw" or "completion-throw") { }
            run(); // Duplicate/late queue invocation must be inert after completion or reset.
            discard();
            Require(actions == (outcome is "success" or "action-throw" or "completion-throw" ? 1 : 0)
                && completions == (replaced ? 0 : 1) && sync.IsBusy == replaced,
                "Incorrect queued ownership cleanup: " + outcome);
            newer?.Dispose();
        }

        var foreign = new ClientSynchronizationCoordinator();
        var owner = new ClientSynchronizationCoordinator();
        Require(owner.TryBeginManual(out var lease), "Producer rejected.");
        Require(!foreign.TryBeginManual(out _, lease), "Foreign coordinator accepted a manual continuation.");
        lease!.Dispose();
        bool queued = false;
        try { lease.QueueContinuation((_, _) => queued = true, _ => { }); }
        catch (InvalidOperationException) { }
        Require(!queued, "Disposed manual operation queued work.");
    }

    private static async Task AsyncManualHandoffs()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(sync.TryBeginManual(out var first), "Async producer rejected.");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action apply = null!, discard = null!;
        int applied = 0;
        Task download = first!.RunContinuation(async operation =>
        {
            entered.SetResult(true);
            await response.Task;
            operation.QueueContinuation((action, cancel) => { apply = action; discard = cancel; }, next =>
            {
                applied++;
                Require(sync.TryBeginManual(out var continued, next) && ReferenceEquals(continued, next),
                    "UI stage lost async ownership.");
            });
        });
        first.Dispose();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(sync.IsBusy && !sync.TryBeginManual(out _), "Caller released a pending download.");
        response.SetResult(true);
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Require(sync.IsBusy && applied == 0, "Async finalizer released the pending UI stage.");
        apply();
        Require(!sync.IsBusy && applied == 1, "UI continuation did not complete once.");
        Require(sync.TryAcquireSnapshot(out var automaticUpload), "Completed entry stage blocked the first automatic snapshot.");
        discard(); apply(); first.Dispose();
        Require(sync.SnapshotInProgress && applied == 1, "Late entry cleanup released the automatic snapshot or reopened the UI.");
        automaticUpload!.Dispose();

        Require(sync.TryBeginManual(out first), "Second producer rejected.");
        Task nested = null!;
        var secondResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await first!.RunContinuation(operation =>
        {
            nested = operation.RunContinuation(async _ => await secondResponse.Task);
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
        Require(sync.IsBusy, "First async stage released its successor.");
        secondResponse.SetResult(true);
        await nested.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!sync.IsBusy, "Second async stage retained admission.");
    }

    private static async Task AsyncManualFailures()
    {
        foreach (bool synchronous in new[] { true, false })
        {
            var sync = new ClientSynchronizationCoordinator();
            Require(sync.TryBeginManual(out var first), "Async producer rejected.");
            Task failed = first!.RunContinuation(_ => synchronous
                ? throw new ApplicationException("before await")
                : FailAfterYield());
            try { await failed.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Failure disappeared."); }
            catch (ApplicationException) { }
            Require(!sync.IsBusy, "Failed async stage retained admission.");
        }

        var owner = new ClientSynchronizationCoordinator();
        Require(owner.TryBeginManual(out var old), "Old producer rejected.");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool applied = false;
        Task stale = old!.RunContinuation(async _ =>
        {
            entered.SetResult(true);
            await ClientSessionWork.Observe(response.Task);
            applied = true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClientSessionWork.Reset(); owner.Reset();
        Require(owner.TryBeginManual(out var current), "New producer rejected.");
        response.SetResult(true);
        await stale.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!applied && owner.IsBusy, "Late async completion affected a new session.");
        current!.Dispose();
    }

    private static async Task FailAfterYield()
    {
        await Task.Yield();
        throw new ApplicationException("after await");
    }

    private static void DeferredDecisionOwnership()
    {
        var sync = new ClientSynchronizationCoordinator();
        Require(sync.TryBeginManual(out var producer), "Decision producer rejected.");
        Action resume = null!, cancel = null!;
        int resumed = 0;
        producer!.QueueContinuation((accept, reject) => { resume = accept; cancel = reject; }, _ => resumed++);
        producer.Dispose();
        Require(sync.IsBusy, "Showing a decision released its admission.");
        cancel();
        Require(sync.TryBeginManual(out var next), "Cancelled decision retained admission.");
        resume(); cancel();
        Require(resumed == 0 && sync.IsBusy, "Old decision affected a replacement operation.");
        next!.Dispose();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
