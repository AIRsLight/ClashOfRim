using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SnapshotPostUploadWorkerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(3);
    private static SnapshotPostUploadJobInput Input => new(SnapshotPostUploadKind.AuthoritativeColonySnapshot,
        "user", "colony", "session", "snapshot", At);

    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CompletionAndReconciliation();
                foreach (bool prepared in new[] { false, true })
                {
                    RetryBudget(prepared);
                    ManualReview(prepared);
                    foreach (string unavailable in new[] { "missing", "kind", "inline", "capability" }) Unavailable(prepared, unavailable);
                }
            }
            LegacyContext();
            CorePreparedRecovery();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static SnapshotPostUploadWorker Worker(SnapshotPostUploadJobRegistry jobs) => new(jobs, () => NullLogger.Instance);
    private static SnapshotPostUploadJobRecord Enqueue(SnapshotPostUploadJobRegistry jobs, bool prepared) => prepared
        ? jobs.EnqueuePrepared("prepared", "processor", Input, "payload") : jobs.Enqueue("processor", Input, "payload");

    private static void CompletionAndReconciliation()
    {
        var jobs = new SnapshotPostUploadJobRegistry(); var worker = Worker(jobs);
        var original = Enqueue(jobs, true); var calls = new List<string>();
        var operation = new SnapshotPostUploadWork(new Processor("processor"), job => new(() =>
        {
            Check(job.State == SnapshotPostUploadJobState.Ready && job.JobId == original.JobId
                && job.PayloadJson == "payload" && job.OccurredAtUtc == At, "Prepared recovery changed job data.");
            calls.Add("process");
        }, () =>
        {
            Check(jobs.Find(original.JobId) is null, "Cleanup ran before durable job removal.");
            calls.Add("complete"); throw new IOException("cleanup failed");
        }), (job, at) => { calls.Add("recover"); jobs.MarkReady(job.JobId, at); }, () => throw new IOException("reconcile failed"));
        bool rejected = false;
        try { worker.ProcessReady([operation, new(operation.Processor)], At); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && calls.Count == 0 && jobs.Find(original.JobId) == original, "Duplicate registration executed recovery first.");
        worker.ReconcileArtifacts([operation, new(new Processor("other", ExecutionMode: SnapshotPostUploadExecutionMode.Inline),
            ReconcileArtifacts: () => calls.Add("reconcile"))]);
        Check(worker.ProcessReady([operation], At) == 1 && jobs.ListAll().Count == 0
            && calls.SequenceEqual(new[] { "reconcile", "recover", "process", "complete" }),
            "Recovery did not execute newly ready work, or cleanup failure retried completed work.");
    }

    private static void RetryBudget(bool prepared)
    {
        var jobs = new SnapshotPostUploadJobRegistry(); var worker = Worker(jobs); var job = Enqueue(jobs, prepared);
        var operation = new SnapshotPostUploadWork(new Processor("processor"), _ => new(() => throw new IOException("failed")),
            (_, _) => throw new IOException("failed"));
        for (int attempt = 0; attempt < SnapshotPostUploadWorker.MaximumAutomaticAttempts; attempt++)
        {
            var before = jobs.Find(job.JobId)!;
            Check(worker.ProcessReady([operation], before.NextAttemptAtUtc.AddTicks(-1)) == 0 && jobs.Find(job.JobId) == before,
                "Work ran before its retry deadline.");
            Check(worker.ProcessReady([operation], before.NextAttemptAtUtc) == 0, "Failed work counted as complete.");
            var failed = jobs.Find(job.JobId)!;
            Check(failed.AttemptCount == attempt + 1 && failed.LastError == "failed", "Retry attempt or error was lost.");
            if (attempt < SnapshotPostUploadWorker.MaximumAutomaticAttempts - 1)
                Check(failed.State == job.State && failed.NextAttemptAtUtc == before.NextAttemptAtUtc.AddSeconds(1 << attempt),
                    "Retry changed phase or exponential delay.");
        }
        var manual = jobs.Find(job.JobId)!;
        Check(manual.State == SnapshotPostUploadJobState.ManualReview && worker.ProcessReady([operation], DateTimeOffset.MaxValue) == 0
            && jobs.Find(job.JobId) == manual, "Exhausted work remained executable.");
        var retried = jobs.MarkReady(job.JobId, At);
        Check(retried.AttemptCount == 0 && worker.ProcessReady([operation with { Bind = _ => new(() => { }) }], At) == 1,
            "Administrator retry did not start a new execution cycle.");
    }

    private static void ManualReview(bool prepared)
    {
        var jobs = new SnapshotPostUploadJobRegistry(); var job = Enqueue(jobs, prepared);
        var operation = new SnapshotPostUploadWork(new Processor("processor"), _ => new(() => throw new SnapshotPostUploadManualReviewException("review")),
            (_, _) => throw new SnapshotPostUploadManualReviewException("review"));
        Check(Worker(jobs).ProcessReady([operation], At) == 0, "Manual review counted as completed work.");
        var result = jobs.Find(job.JobId)!;
        Check(result.AttemptCount == 1 && result.LastError == "review"
            && result.State == (prepared ? SnapshotPostUploadJobState.Prepared : SnapshotPostUploadJobState.ManualReview),
            "Prepared recovery and ready execution lost their distinct review policies.");
    }

    private static void Unavailable(bool prepared, string unavailable)
    {
        var jobs = new SnapshotPostUploadJobRegistry(); var job = Enqueue(jobs, prepared);
        bool executed = false;
        var processor = new Processor(unavailable == "missing" ? "other" : "processor",
            unavailable == "kind" ? SnapshotPostUploadKind.RaidSettlementEvidence : Input.Kind,
            unavailable == "inline" ? SnapshotPostUploadExecutionMode.Inline : SnapshotPostUploadExecutionMode.Deferred);
        var operation = new SnapshotPostUploadWork(processor,
            unavailable == "capability" && !prepared ? null : _ => new(() => executed = true),
            unavailable == "capability" ? null : (_, _) => executed = true);
        Check(Worker(jobs).ProcessReady([operation], At) == 0 && !executed && jobs.Find(job.JobId) is { AttemptCount: 1 } result
            && result.State == job.State && result.NextAttemptAtUtc == At.AddSeconds(1), "Unavailable work bypassed recovery or retry policy.");
    }

    private static void LegacyContext()
    {
        var state = new ClashOfRimNetworkState(); var processor = new LegacyProcessor();
        var job = Enqueue(state.SnapshotPostUploadJobs, true);
        Check(SnapshotPostUploadJobExecutor.ProcessReady(state, [processor], At) == 1, "Public executor lost deferred capabilities.");
        var context = processor.Context!;
        Check(ReferenceEquals(context, processor.CompletedContext) && ReferenceEquals(context.State, state)
            && context.JobId == job.JobId && context.ProcessorId == job.ProcessorId && context.Kind == job.Kind
            && context.UserId == job.UserId && context.ColonyId == job.ColonyId && context.SessionId == job.SessionId
            && context.SnapshotId == job.SnapshotId && context.OccurredAtUtc == job.OccurredAtUtc
            && context.PayloadJson == job.PayloadJson && context.AttemptCount == 0, "Legacy execution and cleanup did not share the original job context.");
    }

    private static void CorePreparedRecovery()
    {
        using var f = new RaidEvidenceModuleTestContext();
        f.SeedPrepared();
        f.Open();
        var state = new ClashOfRimNetworkState(ledger: f.Ledger, snapshotStore: f.Snapshots, playerRegistry: f.Players,
            snapshotPostUploadJobs: f.Jobs, snapshotPostUploadArtifacts: f.Artifacts);
        Check(Worker(f.Jobs).ProcessReady(state.SnapshotPostUploads.ResolveWork(), f.Data.Now) == 1
            && f.Jobs.ListAll().Count == 0 && !f.Artifacts.Exists(f.ArtifactId)
            && f.Ledger.Find(f.Data.Raid.EventId)!.AppliedSnapshotId is not null,
            "Reopened core Prepared work did not recover, settle and clean its artifact in one pass.");
    }

    private record Processor(string Id, SnapshotPostUploadKind Kind = SnapshotPostUploadKind.AuthoritativeColonySnapshot,
        SnapshotPostUploadExecutionMode ExecutionMode = SnapshotPostUploadExecutionMode.Deferred) : ISnapshotPostUploadProcessor
    {
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.Notification;
        public int Order => 0;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public bool Supports(SnapshotPostUploadKind kind) => Kind == kind;
    }

    private sealed record LegacyProcessor() : Processor("processor"), IRecoverableDeferredSnapshotPostUploadProcessor, IDeferredSnapshotPostUploadCompletionHandler
    {
        internal SnapshotPostUploadDeferredContext? Context, CompletedContext;
        public string CapturePayload(SnapshotPostUploadContext context) => throw new Exception("Executor captured a new payload.");
        public void RecoverPrepared(ClashOfRimNetworkState state, SnapshotPostUploadJobRecord job, DateTimeOffset at) => state.SnapshotPostUploadJobs.MarkReady(job.JobId, at);
        public void ProcessDeferred(SnapshotPostUploadDeferredContext context) => Context = context;
        public void OnDeferredCompleted(SnapshotPostUploadDeferredContext context) => CompletedContext = context;
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
