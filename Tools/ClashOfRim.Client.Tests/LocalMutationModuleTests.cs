using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class LocalMutationModuleTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        ClientSessionWork.Reset();
        try
        {
            AdmissionAcrossKinds();
            FailureDecisions();
            ReplacementAndReset();
            CallbackFailures();
            await ConcurrentDecisions();
            await ExpiredWork();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void AdmissionAcrossKinds()
    {
        var mutations = new ClientLocalMutationCoordinator();
        Require(!mutations.IsPending && mutations.Current is null, "Fresh module has a pending mutation.");
        mutations.UpdateStatus("ignored");
        Require(!mutations.IsPending, "Status alone began a mutation.");
        Require(mutations.CheckAdmission(false, false) == LocalMutationAdmission.NotConfigured
            && mutations.CheckAdmission(true, false) == LocalMutationAdmission.SnapshotMissing
            && mutations.CheckAdmission(true, true) == LocalMutationAdmission.Allowed, "Empty-session admission changed.");
        foreach (LocalMutationKind kind in Enum.GetValues<LocalMutationKind>())
        {
            mutations.Begin(kind, "等待确认");
            var captured = mutations.Current!;
            Require(captured.Kind == kind && captured.Status == "等待确认", "Mutation identity or display state was lost.");
            Require(mutations.CheckAdmission(false, false, kind) == LocalMutationAdmission.NotConfigured,
                "A continuation bypassed configuration validation.");
            Require(mutations.CheckAdmission(true, false) == LocalMutationAdmission.PendingMutation
                && mutations.CheckAdmission(true, true) == LocalMutationAdmission.PendingMutation,
                "Ordinary work was admitted with an unconfirmed mutation.");
            Require(mutations.CheckAdmission(true, false, kind) == LocalMutationAdmission.SnapshotMissing,
                "A continuation bypassed the authoritative snapshot requirement.");
            Require(mutations.CheckAdmission(true, true, kind) == LocalMutationAdmission.Allowed,
                "The current operation was blocked from confirming its own mutation.");
            foreach (var other in Enum.GetValues<LocalMutationKind>().Where(k => k != kind))
                Require(mutations.CheckAdmission(true, true, other) == LocalMutationAdmission.PendingMutation
                    && mutations.BlockingMutation(other) == captured, "A different operation bypassed the pending mutation.");
            mutations.UpdateStatus("Confirmation en attente");
            Require(mutations.CheckAdmission(true, true, kind) == LocalMutationAdmission.Allowed
                && mutations.BlockingMutation(kind) is null && mutations.Current!.Status == "Confirmation en attente"
                && captured.Status == "等待确认", "Display updates changed admission identity or mutated a captured view.");
            mutations.Reset();
            Require(!mutations.IsPending && mutations.CheckAdmission(true, true) == LocalMutationAdmission.Allowed,
                "Reset left local work blocked.");
        }
    }

    private static void FailureDecisions()
    {
        var mutations = new ClientLocalMutationCoordinator();
        int retries = 0, exits = 0;
        var rejected = mutations.RecordFailure(LocalMutationKind.Bank, "rejected", () => retries++, false);
        Require(!mutations.TryRetry(rejected) && retries == 0 && mutations.IsCurrent(rejected),
            "Non-retryable failure admitted a retry or consumed the exit option.");
        Require(mutations.TryExit(rejected, () => { Require(!mutations.IsPending, "Exit observed pending state."); exits++; })
            && exits == 1 && !mutations.TryExit(rejected, () => exits++), "Exit was not consumed once.");

        ClientLocalMutationCoordinator.Failure? next = null;
        var failed = mutations.RecordFailure(LocalMutationKind.Gift, "network", () =>
        {
            retries++;
            Require(mutations.IsPending && mutations.CheckAdmission(true, true, LocalMutationKind.Gift) == LocalMutationAdmission.Allowed,
                "Retry lost pending state or cannot enter its own confirmation.");
            next = mutations.RecordFailure(LocalMutationKind.Gift, "failed again", () => retries++, true);
        }, true);
        Require(mutations.TryRetry(failed) && retries == 1 && next is not null && mutations.IsCurrent(next),
            "Retry did not preserve the newly reported failure.");
        Require(!mutations.TryRetry(failed) && !mutations.TryExit(failed, () => exits++)
            && retries == 1 && exits == 1 && mutations.IsCurrent(next!), "Old window affected the replacement failure.");
        Require(mutations.TryRetry(next!) && retries == 2 && mutations.IsPending,
            "Retry completed local changes before the snapshot was accepted.");
        mutations.Reset();
    }

    private static void ReplacementAndReset()
    {
        var mutations = new ClientLocalMutationCoordinator();
        Action unexpected = () => throw new Exception("Stale failure callback ran.");
        var old = mutations.RecordFailure(LocalMutationKind.Bank, "first", unexpected, true);
        mutations.Begin(LocalMutationKind.Bank, "upload resumed");
        Require(!mutations.IsCurrent(old) && !mutations.TryRetry(old) && !mutations.TryExit(old, unexpected),
            "Same-kind continuation retained an old window's decisions.");
        old = mutations.RecordFailure(LocalMutationKind.Bank, "second", unexpected, true);
        mutations.Begin(LocalMutationKind.Trade, "trade");
        Require(!mutations.TryRetry(old) && !mutations.TryExit(old, unexpected)
            && mutations.Current!.Kind == LocalMutationKind.Trade, "Old failure affected another kind.");
        old = mutations.RecordFailure(LocalMutationKind.Trade, "third", unexpected, true);
        mutations.Reset();
        mutations.Begin(LocalMutationKind.Trade, "new trade");
        Require(!mutations.TryRetry(old) && !mutations.TryExit(old, unexpected)
            && mutations.Current!.Status == "new trade", "Reset did not retire the old window.");
    }

    private static void CallbackFailures()
    {
        var mutations = new ClientLocalMutationCoordinator();
        var error = new InvalidOperationException("retry fixture");
        ClientLocalMutationCoordinator.Failure? next = null;
        var failed = mutations.RecordFailure(LocalMutationKind.Support, "network", () =>
        {
            next = mutations.RecordFailure(LocalMutationKind.Support, "replacement", () => { }, false);
            throw error;
        }, true);
        try { mutations.TryRetry(failed); throw new Exception("Retry exception was swallowed."); }
        catch (InvalidOperationException ex) when (ReferenceEquals(ex, error)) { }
        Require(!mutations.TryRetry(failed) && mutations.IsCurrent(next!) && mutations.IsPending,
            "Failed retry cleared its replacement or could run twice.");
        try
        {
            mutations.TryExit(next!, () => { mutations.Begin(LocalMutationKind.Shop, "next"); throw error; });
            throw new Exception("Exit exception was swallowed.");
        }
        catch (InvalidOperationException ex) when (ReferenceEquals(ex, error)) { }
        Require(mutations.Current!.Kind == LocalMutationKind.Shop && !mutations.IsCurrent(next!),
            "Exit cleanup cleared subsequently started work.");
    }

    private static async Task ConcurrentDecisions()
    {
        var mutations = new ClientLocalMutationCoordinator();
        int retries = 0, exits = 0;
        var failed = mutations.RecordFailure(LocalMutationKind.Raid, "network", () => Interlocked.Increment(ref retries), true);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            i % 2 == 0 ? mutations.TryRetry(failed) : mutations.TryExit(failed, () => Interlocked.Increment(ref exits)))));
        Require(results.Count(accepted => accepted) == 1 && retries + exits == 1
            && mutations.IsPending == (retries == 1), "Concurrent decisions were not resolved once.");
    }

    private static async Task ExpiredWork()
    {
        var mutations = new ClientLocalMutationCoordinator();
        Action unexpected = () => throw new Exception("Expired callback ran.");
        var failed = mutations.RecordFailure(LocalMutationKind.Support, "old session", unexpected, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool checkedOld = false, refusedPublication = false;
        var oldWork = ClientSessionWork.Run(async () =>
        {
            started.SetResult();
            await release.Task;
            Require(!mutations.IsCurrent(failed) && !mutations.TryRetry(failed) && !mutations.TryExit(failed, unexpected),
                "An expired context still owned its failure.");
            checkedOld = true;
            try { mutations.Begin(LocalMutationKind.Bank, "late publication"); }
            catch (OperationCanceledException) { refusedPublication = true; }
        });
        await started.Task;
        ClientSessionWork.Reset();
        Require(!mutations.IsCurrent(failed) && !mutations.TryExit(failed, unexpected),
            "New session accepted a failure from the old generation.");
        mutations.Reset(); // Same subscription used by ClientSessionLifecycle.Ended.
        mutations.Begin(LocalMutationKind.Support, "new session");
        release.SetResult();
        await oldWork;
        Require(checkedOld && refusedPublication && mutations.Current!.Status == "new session",
            "Expired work overwrote the new session's mutation.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
