using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SnapshotPostUploadRunnerTests
{
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                OrderingAndValidation();
                foreach (var mode in new[] { SnapshotPostUploadExecutionMode.Inline, SnapshotPostUploadExecutionMode.Deferred })
                foreach (var failure in new[] { SnapshotPostUploadFailureMode.AbortPipeline, SnapshotPostUploadFailureMode.ContinuePipeline })
                    CapabilityBoundary(mode, failure);
                DeferredPublication();
            }
            LegacyContext();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static SnapshotPostUploadInput Input() => new(SnapshotPostUploadKind.AuthoritativeColonySnapshot, null!,
        "user", "colony", "session", DateTimeOffset.UnixEpoch.AddDays(3), SnapshotPostUploadExtraData.Empty, false);

    private static void OrderingAndValidation()
    {
        var calls = new List<string>();
        var runner = new SnapshotPostUploadRunner(new(), () => NullLogger.Instance);
        SnapshotPostUploadOperation Work(string id, int order = 0) => new(new Metadata(id) { Order = order }, _ => calls.Add(id));
        runner.Run(Input(), [Work("ı"), Work("i"), Work("I"), Work("first", -1)]);
        Check(calls.SequenceEqual(new[] { "first", "I", "i", "ı" }), "Pipeline lost ordinal ordering.");
        foreach (string invalid in new[] { " ", "duplicate" })
        {
            calls.Clear();
            var bad = new SnapshotPostUploadOperation(new Metadata(invalid) { Kind = SnapshotPostUploadKind.RaidSettlementEvidence });
            ExpectFailure(() => runner.Run(Input(), [Work("first"), Work("duplicate"), bad]));
            Check(calls.Count == 0, "Registration validation ran after a processor, or skipped an unsupported kind.");
        }
    }

    private static void CapabilityBoundary(SnapshotPostUploadExecutionMode mode, SnapshotPostUploadFailureMode failure)
    {
        var calls = new List<string>();
        var runner = new SnapshotPostUploadRunner(new(), () => NullLogger.Instance);
        var invalid = new SnapshotPostUploadOperation(new Metadata("invalid") { ExecutionMode = mode, FailureMode = failure });
        SnapshotPostUploadOperation[] operations = [new(new Metadata("first") { Order = -1 }, _ => calls.Add("first")),
            invalid, new(new Metadata("last") { Order = 1 }, _ => calls.Add("last"))];
        if (failure == SnapshotPostUploadFailureMode.AbortPipeline) ExpectFailure(() => runner.Run(Input(), operations));
        else runner.Run(Input(), operations);
        Check(calls.SequenceEqual(failure == SnapshotPostUploadFailureMode.AbortPipeline ? new[] { "first" } : new[] { "first", "last" }),
            "A missing capability failed eagerly or ignored its failure policy.");
        calls.Clear();
        runner.Run(Input(), [invalid with { Processor = new Metadata("unsupported") { Kind = SnapshotPostUploadKind.RaidSettlementEvidence, ExecutionMode = mode } }, operations[2]]);
        Check(calls.SequenceEqual(new[] { "last" }), "Unsupported processor capability was evaluated.");
    }

    private static void DeferredPublication()
    {
        var jobs = new SnapshotPostUploadJobRegistry();
        var runner = new SnapshotPostUploadRunner(jobs, () => NullLogger.Instance);
        var input = Input(); int scheduled = 0;
        var transactional = new SnapshotPostUploadOperation(new Metadata("scheduled") { ExecutionMode = SnapshotPostUploadExecutionMode.Deferred },
            CapturePayload: _ => throw new Exception("Scheduled operation captured a generic payload."), Schedule: actual =>
            { Check(ReferenceEquals(input, actual), "Scheduling lost input ownership."); scheduled++; });
        var generic = new SnapshotPostUploadOperation(new Metadata("generic") { ExecutionMode = SnapshotPostUploadExecutionMode.Deferred },
            CapturePayload: actual => { Check(ReferenceEquals(input, actual), "Capture lost input ownership."); return "original-payload"; });
        runner.Run(input, [transactional, generic]);
        runner.Run(input, [generic]);
        var job = jobs.ListReady(input.OccurredAtUtc).Single();
        Check(scheduled == 1 && job.ProcessorId == "generic" && job.PayloadJson == "original-payload"
            && job.UserId == input.UserId && job.ColonyId == input.ColonyId && job.SessionId == input.SessionId
            && job.OccurredAtUtc == input.OccurredAtUtc && job.SnapshotId == input.Job.ResolvedSnapshotId,
            "Deferred enqueue duplicated work or changed original job identity.");
    }

    private static void LegacyContext()
    {
        var state = new ClashOfRimNetworkState();
        var input = Input();
        var context = new SnapshotPostUploadContext(state, input.Kind, input.Snapshot, input.UserId, input.ColonyId,
            input.SessionId, input.OccurredAtUtc, input.ExtraData, input.RegisterPlayerColonySite);
        var first = new LegacyProcessor("first"); var last = new LegacyProcessor("last");
        SnapshotPostUploadPipeline.Run(context, [last, null!, first]);
        Check(ReferenceEquals(first.Context, context) && ReferenceEquals(last.Context, context), "Legacy plugin contexts were cloned or redirected.");
        first.ExecutionMode = SnapshotPostUploadExecutionMode.Deferred;
        SnapshotPostUploadPipeline.Run(context, [first]);
        Check(ReferenceEquals(first.Context, context) && first.Captures == 1 && state.SnapshotPostUploadJobs.ListReady(input.OccurredAtUtc).Count == 1,
            "Legacy deferred capture lost its context or durable enqueue.");
    }

    private class Metadata(string id) : ISnapshotPostUploadProcessor
    {
        public string Id => id;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.DerivedMetrics;
        public int Order { get; init; }
        public SnapshotPostUploadFailureMode FailureMode { get; init; } = SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode { get; set; }
        public SnapshotPostUploadKind Kind { get; init; } = SnapshotPostUploadKind.AuthoritativeColonySnapshot;
        public bool Supports(SnapshotPostUploadKind kind) => kind == Kind;
    }

    private sealed class LegacyProcessor(string id) : Metadata(id), IInlineSnapshotPostUploadProcessor, IDeferredSnapshotPostUploadProcessor
    {
        internal SnapshotPostUploadContext? Context;
        internal int Captures;
        public void Process(SnapshotPostUploadContext context) => Context = context;
        public string CapturePayload(SnapshotPostUploadContext context) { Context = context; Captures++; return "legacy"; }
        public void ProcessDeferred(SnapshotPostUploadDeferredContext context) => throw new Exception("Pipeline executed background work inline.");
    }

    private static void ExpectFailure(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected pipeline rejection."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
