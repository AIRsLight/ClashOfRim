using System.Globalization;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
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
                ActiveRegistrations();
                foreach (bool prepared in new[] { false, true }) UnifiedRaidInput(prepared);
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

    private static void ActiveRegistrations()
    {
        const string packageId = "test.post-upload";
        var processor = new LegacyProcessor("test.dynamic");
        var baseline = new CompatibilityBaselineRegistry();
        ClashOfRimNetworkState State(ISnapshotPostUploadProcessor plugin) => new(compatibilityBaselineRegistry: baseline,
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([new(packageId, "test", "1", "test", "", [],
                RequiredPackageIds: [packageId]) { SnapshotPostUploadProcessors = [plugin] }]));
        var state = State(processor);
        state.SnapshotPostUploads.ValidateRegistrations();
        bool Active() => state.SnapshotPostUploads.ResolveWork().Any(work => ReferenceEquals(work.Processor, processor));
        Check(!Active(), "Inactive plugin entered the background processor source.");
        baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = packageId }] }, "admin", DateTimeOffset.UnixEpoch);
        Check(Active(), "Processor source did not activate the configured plugin.");
        baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", DateTimeOffset.UnixEpoch.AddDays(1));
        Check(!Active(), "Processor source retained a removed plugin.");
        var duplicate = State(new LegacyProcessor("core.latest-snapshot-reference"));
        ExpectFailure(duplicate.SnapshotPostUploads.ValidateRegistrations);
    }

    private static void UnifiedRaidInput(bool prepared)
    {
        using var f = new RaidEvidenceModuleTestContext(prepared);
        var first = new LegacyProcessor("first") { Kind = SnapshotPostUploadKind.RaidSettlementEvidence };
        var last = new LegacyProcessor("last") { Kind = SnapshotPostUploadKind.RaidSettlementEvidence };
        var state = new ClashOfRimNetworkState(ledger: f.Ledger, snapshotStore: f.Snapshots, playerRegistry: f.Players,
            snapshotPostUploadJobs: f.Jobs, snapshotPostUploadArtifacts: f.Artifacts,
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.raid-input", "test", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [last, first] }]));
        void Run()
        {
            if (!prepared) { state.SnapshotPostUploads.Run(f.Request, null); return; }
            RaidEvidenceSchedulePreparation preparation;
            using (state.SnapshotMutations.EnterUnrecovered())
                lock (f.Jobs.SnapshotCommitGate) preparation = state.RaidEvidenceScheduling.Capture(f.Request);
            using (preparation)
            {
                preparation.Prepare(null);
                using (state.SnapshotMutations.EnterUnrecovered())
                    lock (f.Jobs.SnapshotCommitGate) state.SnapshotPostUploads.Run(f.Request, preparation);
            }
        }
        Run();
        var job = f.Jobs.ListAll().Single();
        f.CheckPublished(job);
        var context = first.Context!;
        var extra = context.ExtraData.RaidSettlement!;
        Check(ReferenceEquals(context, last.Context) && ReferenceEquals(context.Snapshot, f.Request.Snapshot)
            && context.Kind == f.Request.Job.Kind && context.UserId == f.Request.Job.UserId && context.ColonyId == f.Request.Job.ColonyId
            && context.SessionId == f.Request.Job.SessionId && context.OccurredAtUtc == f.Request.Job.OccurredAtUtc
            && !context.RegisterPlayerColonySite && context.ExtraData.SnapshotUploadKind == SnapshotUploadKinds.RaidSettlementEvidence
            && extra.RaidEventId == f.Request.RaidEventId && ReferenceEquals(extra.EvidencePayload, f.Request.EvidencePayload)
            && extra.Origin == f.Request.Origin && extra.ClientApplicationResult == f.Request.ClientApplicationResult
            && extra.RequestHash == f.Request.RequestHash && (extra.PreparedSchedule is not null) == prepared,
            "Unified raid entry changed evidence identity, preparation or the shared plugin context.");
        Run();
        Check(f.Jobs.ListAll().Single() == job, "Unified raid replay replaced the original job.");
        f.CheckPublished(job);
        var worker = new SnapshotPostUploadWorker(f.Jobs, () => NullLogger.Instance);
        Check(worker.ProcessReady(state.SnapshotPostUploads.ResolveWork(), f.Data.Now) == 1
            && f.Jobs.ListAll().Count == 0 && !f.Artifacts.Exists(f.ArtifactId)
            && f.Ledger.Find(f.Data.Raid.EventId)!.AppliedSnapshotId is not null,
            "Core raid work did not settle, remove its job and clean immutable evidence.");
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
