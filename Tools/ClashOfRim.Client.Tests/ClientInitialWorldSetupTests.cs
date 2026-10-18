using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientInitialWorldSetupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool playing in new[] { false, true })
                foreach (bool session in new[] { false, true })
                foreach (bool presence in new[] { false, true }) { await Success(playing, session, presence); count++; }
                foreach (string gate in new[] { "manual", "snapshot", "missing-world", "not-configured", "invalid-world", "no-initial" }) { await Admission(gate); count++; }
                foreach (string phase in new[] { "metadata", "world-upload", "requirements", "admin-submit", "snapshot-upload" })
                foreach (string fault in new[] { "network", "missing-response", "missing-result", "rejected", "exception", "canceled" }) { await Failure(phase, fault); count++; }
                foreach (string phase in new[] { "source", "metadata", "world-upload", "requirements", "admin-capture", "admin-submit", "baseline-text", "effect-Markers", "save", "snapshot-upload", "store", "snapshot-text", "effect-Events" })
                foreach (string change in new[] { "user", "auth", "colony", "server", "game", "world", "login", "setup", "work", "unavailable" })
                { await Ownership(phase, change); count++; }
                foreach (string phase in new[] { "metadata", "baseline-text", "save", "snapshot-upload", "snapshot-text", "effect-Events" }) { await Lineage(phase); count++; }
                foreach (string phase in new[] { "source", "admin-capture", "baseline-text", "effect-Markers", "save", "snapshot-text", "effect-Players" }) { await CallbackFailure(phase); count++; }
                foreach (int discard in new[] { 1, 2, 3 }) { await Discard(discard); count++; }
                await Replacement(); await PromotionReset(); await PromotionFailure(); await InGameExit(); await FailureReceipt(); count += 5;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: initial world setup ({count} scenarios)");
    }
    private static async Task Success(bool playing, bool session, bool presence)
    {
        var f = new Fixture(playing, session) { Presence = presence, RepeatQueue = true };
        Task work = f.Start(); await f.Start(); await f.Ports.PumpUntil(work);
        bool snapshot = playing && session;
        Check(f.Calls.SequenceEqual(snapshot ? new[] { "metadata", "world-upload", "requirements", "admin-submit", "snapshot-upload" }
            : new[] { "metadata", "world-upload", "requirements", "admin-submit" }), "Initial setup duplicated or reordered requests.");
        Check(f.Successes == (snapshot ? 2 : 1) && !f.Sync.IsBusy && !f.Setup.Pending, "Successful setup lost completion/admission.");
        Check(f.Effects.SequenceEqual(snapshot ? Enum.GetValues<ClientSessionFollowup>().Where(e => !presence || e != ClientSessionFollowup.Presence)
            : new[] { ClientSessionFollowup.Markers }), "Snapshot handoff effects changed order or ran outside Playing/session.");
        Check(f.Source.Calls == 8 && f.Ports.Captures == (snapshot ? 1 : 0) && f.Ports.Published == (snapshot ? 1 : 0), "Repeated queue callback captured/published twice.");
        if (snapshot) Check(f.PromotionObserved && f.UploadKind == ModSnapshotUploadKinds.InitialColonySnapshot && f.Ports.CaptureModes.SequenceEqual(new[] { false }), "Snapshot did not promote the existing manual lease or changed its purpose.");
        int calls = f.Calls.Count; await f.Start(); Check(f.Calls.Count == calls, "Consumed initial setup restarted on another lifecycle callback.");
    }
    private static async Task Admission(string gate)
    {
        var f = new Fixture(); ClientSynchronizationCoordinator.Lease? lease = null;
        switch (gate)
        {
            case "manual": Check(f.Sync.TryBeginManual(out lease), "Fixture manual admission."); break;
            case "snapshot": Check(f.Sync.TryAcquireSnapshot(out lease), "Fixture snapshot admission."); break;
            case "missing-world": f.HasWorld = false; break;
            case "not-configured": f.Ports.Colony = ""; f.Prepare(); break;
            case "invalid-world": f.InvalidWorld = true; break;
            case "no-initial": f.Setup.Reset(); break;
        }
        await f.Ports.PumpUntil(f.Start());
        Check(f.Calls.IsEmpty && f.Successes == 0, "Admission/invalid capture sent a request: " + gate);
        bool retry = gate is "manual" or "snapshot" or "missing-world";
        Check(f.Setup.Pending == retry, "Pending initial state was lost/restored incorrectly: " + gate);
        if (retry)
        {
            lease?.Dispose(); f.HasWorld = true; await f.Ports.PumpUntil(f.Start());
            Check(f.Successes == 2 && !f.Sync.IsBusy, "Deferred setup did not recover after admission/world readiness.");
        }
    }
    private static async Task Failure(string phase, string fault)
    {
        var f = new Fixture { FaultPhase = phase, Fault = fault };
        await f.Ports.PumpUntil(f.Start());
        string[] order = { "metadata", "world-upload", "requirements", "admin-submit", "snapshot-upload" };
        Check(f.Calls.SequenceEqual(order.Take(Array.IndexOf(order, phase) + 1)), "Failed setup continued to another network stage: " + phase + "/" + fault);
        // The established uploader accepts an absent optional Result; this extraction does not change that contract.
        bool snapshotAccepted = phase == "snapshot-upload" && fault == "missing-result";
        Check(f.Successes == (snapshotAccepted ? 2 : phase == "snapshot-upload" ? 1 : 0) && !f.Sync.IsBusy, "Failure leaked admission or published setup success.");
        Check(f.Effects.Count == (snapshotAccepted ? 7 : phase == "snapshot-upload" ? 1 : 0), "Failure launched unrelated follow-up effects.");
        if (!snapshotAccepted) Check(f.Statuses.Any(s => s.Contains(phase == "snapshot-upload" ? "InitialSnapshot" : phase == "metadata" || phase == "world-upload" ? "WorldConfigFailed" : "AdminBaselineFailed"))
            || f.Statuses.Any(s => s.Contains("Exception")), "Failure was not reported by its stage.");
    }
    private static async Task Ownership(string phase, string change)
    {
        var f = new Fixture(); bool changed = false; int calls = 0, effects = 0, messages = 0;
        f.Hook = step =>
        {
            if (step != phase || changed) return;
            changed = true; calls = f.Calls.Count; effects = f.Effects.Count; messages = f.Successes; f.Change(change);
        };
        await f.Ports.PumpUntil(f.Start());
        Check(changed && f.Calls.Count == calls && f.Effects.Count == effects && f.Successes == messages,
            "Obsolete setup continued: " + phase + "/" + change);
        Check(!f.Sync.IsBusy, "Obsolete setup retained synchronization admission: " + phase + "/" + change);
    }
    private static async Task Lineage(string phase)
    {
        var f = new Fixture(); bool changed = false; int effects = 0;
        f.Hook = step => { if (step == phase && !changed) { changed = true; effects = f.Effects.Count; f.Ports.Snapshot = phase is "snapshot-text" or "effect-Events" ? "base" : "advanced"; } };
        await f.Ports.PumpUntil(f.Start());
        bool allowed = phase is "metadata" or "baseline-text";
        Check(changed && !f.Sync.IsBusy && (allowed ? f.Successes == 2 && f.Effects.Count == 7 : f.Effects.Count == Math.Max(1, effects)), "Snapshot lineage boundary changed: " + phase);
    }
    private static async Task CallbackFailure(string phase)
    {
        var f = new Fixture(); bool threw = false;
        f.Hook = step => { if (step == phase && !threw) { threw = true; throw new InvalidOperationException("callback failure"); } };
        await f.Ports.PumpUntil(f.Start(), ex => { if (ex is not InvalidOperationException) throw ex; });
        Check(threw && !f.Sync.IsBusy && f.Diagnostics.Count > 0, "Callback failure leaked or escaped reporting: " + phase);
        f.Hook = null; f.Prepare(); await f.Ports.PumpUntil(f.Start());
        Check(!f.Sync.IsBusy && f.Effects.Last() == ClientSessionFollowup.Presence, "Callback failure prevented a new setup attempt.");
    }
    private static async Task Discard(int index)
    {
        var f = new Fixture { DiscardIndex = index, RepeatQueue = true };
        await f.Ports.PumpUntil(f.Start());
        Check(!f.Sync.IsBusy && f.Effects.Count <= 1 && f.Successes <= (index == 3 ? 1 : 0), "Discarded/repeated setup publication launched effects.");
    }
    private static async Task Replacement()
    {
        var f = new Fixture(); var held = Signal(); var release = Signal(); int metadata = 0;
        f.Before = async phase => { if (phase == "metadata" && Interlocked.Increment(ref metadata) == 1) { held.TrySetResult(); await release.Task; } };
        var old = f.Start(); await held.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.World = new(); f.Prepare(); var next = f.Start();
        var queued = await f.Ports.Next(); // New administrator capture; successor owns the manual lease.
        release.TrySetResult(); await old.WaitAsync(TimeSpan.FromSeconds(4));
        Check(f.Sync.ManualInProgress && !f.Sync.SnapshotInProgress, "Old finally released the successor's manual lease.");
        queued(); await f.Ports.PumpUntil(next);
        Check(f.Successes == 2 && f.Calls.Count(c => c == "snapshot-upload") == 1 && !f.Sync.IsBusy, "Replacement failed to complete independently.");
    }
    private static async Task PromotionReset()
    {
        var f = new Fixture(); bool reset = false;
        f.Sync.SnapshotChanged += notice => { if (notice.Phase == SnapshotTransactionPhase.Started && !reset) { reset = true; f.Setup.Reset(); } };
        await f.Ports.PumpUntil(f.Start());
        Check(reset && f.Ports.Captures == 0 && !f.Sync.IsBusy && f.Effects.Count == 1, "Reset during promotion leaked the successor lease or started capture.");
    }
    private static async Task InGameExit()
    {
        var f = new Fixture(); f.Hook = phase => { if (phase == "snapshot-upload") f.Playing = false; };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Ports.Published == 0 && f.Effects.Count == 1 && !f.Sync.IsBusy, "Leaving Playing during upload published runtime effects.");
    }
    private static async Task PromotionFailure()
    {
        var f = new Fixture();
        f.Sync.SnapshotChanged += notice => { if (notice.Phase == SnapshotTransactionPhase.Started) throw new InvalidOperationException("promotion observer failed"); };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Ports.Captures == 0 && !f.Sync.IsBusy && f.Diagnostics.Count > 0, "Promotion callback failure leaked the newly acquired snapshot lease or lost its diagnostic.");
    }
    private static async Task FailureReceipt()
    {
        var f = new Fixture { FaultPhase = "requirements", Fault = "rejected" };
        f.Hook = phase => { if (phase == "queue-1") f.LastProtocol!.Message = "changed"; };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Statuses.Any(s => s.Contains("expected")) && f.Statuses.All(s => !s.Contains("changed")), "Queued failure borrowed a mutable response message.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldBaselineRuntime, IClientWorldBaselineTransport, IClientStorytellerRuntime,
        IClientAdminBaselineTransport, IClientSnapshotTransport, IClientInitialWorldRuntime, IClientInitialWorldPresentation, IClientSessionFollowups
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldSetupState Setup;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientAdminBaselineCaptureTests.Source Source = new();
        internal readonly ClientInitialWorldSetup Module;
        internal readonly ConcurrentQueue<string> Trace = new(), Calls = new(), Statuses = new(), Diagnostics = new();
        internal readonly List<ClientSessionFollowup> Effects = new();
        internal object World = new(); internal bool HasWorld = true, Playing, Presence, RepeatQueue, InvalidWorld, PromotionObserved;
        internal string? FaultPhase, Fault, UploadKind; internal int Successes, DiscardIndex, QueueIndex;
        internal ModProtocolResponseDto? LastProtocol;
        internal Action<string>? Hook; internal Func<string, Task>? Before;
        internal Fixture(bool playing = true, bool session = true)
        {
            ClientSessionWork.Reset(); Playing = playing; Session = new(Ports, Ports, new(Ports));
            Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = session ? "session" : null });
            Setup = new(Session); Prepare();
            var submission = new ClientWorldBaselineSubmission(Session, this, this);
            var storyteller = new ClientStorytellerConfiguration(Session, Setup, this);
            Module = new(Session, Setup, Sync, new(submission, storyteller), submission, new(Session, Source, Enqueue), this,
                new ClientSnapshotUploader(Ports, Ports, this, Sync), this, this, this);
            Source.After = step => { if (step % 8 == 1) Step("admin-capture"); };
            Ports.OnCapture = () => Step("save"); Ports.OnPublish = () => Step("store");
            Sync.SnapshotChanged += notice => { if (notice.Phase == SnapshotTransactionPhase.Started) PromotionObserved = true; };
        }
        internal void Prepare() => Setup.Prepare(new() { Result = new() { Accepted = true }, RequiresInitialWorldConfiguration = true });
        internal Task Start() => Module.StartAsync(new() { ColorHex = "#123456" });
        public void AdoptQueue(ModEventQueueSummaryDto? queue) => throw new NotSupportedException();
        public Task Dispatch(ClientSessionFollowup effect, ClientSessionFollowupSource source, Func<Action, bool> step)
        {
            Check(source == ClientSessionFollowupSource.InitialWorld, "Wrong followup source.");
            step(() =>
            {
                if (effect != ClientSessionFollowup.Markers) Check(!Sync.IsBusy, "Follow-up effects ran before releasing snapshot admission.");
                else Check(Sync.ManualInProgress && !Sync.SnapshotInProgress, "Baseline publication released manual admission before the snapshot handoff.");
                Effects.Add(effect); Step("effect-" + effect);
            });
            return Task.CompletedTask;
        }
        internal void Step(string stage) { Trace.Enqueue(stage); Hook?.Invoke(stage); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "colony": Ports.Colony = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break;
                case "setup": Prepare(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "work": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
            }
        }
        public bool IsPlaying => Playing; public bool IsPresenceRunning => Presence;
        public void Enqueue(Action action, Action discard)
        {
            int index = ++QueueIndex; Step("queue-" + index);
            Ports.Enqueue(() => { if (index == DiscardIndex) { discard(); if (RepeatQueue) action(); } else { action(); if (RepeatQueue) action(); } });
        }
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            if (key == "ClashOfRim.WorldBaseline.StatusSubmitted") Step("baseline-text");
            if (key == "ClashOfRim.WorldBaseline.StatusInitialSnapshotUploaded") Step("snapshot-text");
            return key + " " + string.Join(";", args.Select(a => a.Value));
        }
        public string CaptureFailure(ClientWorldBaselineCapture.Failure failure) => failure.Code;
        public string SnapshotFailure(SnapshotUploadFailure failure) => failure.Message ?? failure.Code;
        public void Status(string text) => Statuses.Enqueue(text);
        public void SnapshotStatus(string text) => Statuses.Enqueue(text);
        public void Message(string text, ClientWorldWriteMessage kind) { if (kind == ClientWorldWriteMessage.Success) Successes++; }
        public void Diagnose(string text) => Diagnostics.Enqueue(text);
        IClientWorldBaselineSession IClientWorldBaselineRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        private async Task<string?> Network(string phase)
        { Calls.Enqueue(phase); string? fault = phase == FaultPhase ? Fault : null; Step(phase); if (Before is not null) await Before(phase); return fault; }
        public async Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
        { string? fault = await Network("metadata"); return Result(fault, new ModSubmitWorldConfigurationResponseDto { Result = Protocol(fault), WorldConfiguration = configuration }); }
        public async Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token)
        { string? fault = await Network("world-upload"); return Result(fault, new ModUploadWorldSubstrateResponseDto { Result = Protocol(fault) }); }
        public async Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> RequirementsAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        { string? fault = await Network("requirements"); return Result(fault, new ModGetAdminBaselineRequirementsResponseDto { Result = Protocol(fault), BaselineExtensions = new() { new() { ProviderId = "provider", Kind = "kind" } } }); }
        public async Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModSubmitAdminBaselineRequestDto request, CancellationToken token)
        { string? fault = await Network("admin-submit"); return Result(fault, new ModSubmitAdminBaselineResponseDto { Result = Protocol(fault), StandardMarketValueCount = 1, TrapAutoApprovedCount = 1, TrapCandidateCount = 1 }); }
        public async Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key, ModSnapshotPackageMetadataDto package,
            byte[] payload, string? confirmation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Check(Sync.SnapshotInProgress && confirmation is null && achievements.Count == 0, "Snapshot bypassed shared admission or acquired unrelated confirmation.");
            UploadKind = package.SnapshotUploadKind; string? fault = await Network("snapshot-upload");
            return Result(fault, new ModUploadSnapshotResponseDto { Result = Protocol(fault), AcceptedSnapshotId = "accepted", NextLineageToken = "lineage" });
        }
        private ModProtocolResponseDto? Protocol(string? fault) => LastProtocol = fault == "missing-result" ? null : new() { Accepted = fault != "rejected", ErrorCode = 17, Message = "expected" };
        private static ClashOfRimClientNetworkResult<T> Result<T>(string? fault, T response) => fault switch
        { "network" => ClashOfRimClientNetworkResult<T>.Failed("network", "expected"), "missing-response" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new InvalidOperationException("expected"), "canceled" => throw new TaskCanceledException("expected"), _ => ClashOfRimClientNetworkResult<T>.Ok(response) };
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldBaselineSession, IClientStorytellerSession
        {
            public bool IsCurrent => F.HasWorld && ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public string Language => "French"; public bool IsPlaying => F.Playing;
            public ModWorldConfigurationDto ReadWorld(string user, string colony, ModColonyAppearanceDto? appearance)
            {
                F.Step("source"); return new() { SeedString = F.InvalidWorld ? "" : "种子 İ", PlanetCoverage = "0.3", GameLanguage = "French",
                    FactionDefNames = new() { "Tribe" }, Features = new() { new() { DefName = "Island", Label = "île" } },
                    TileGeometry = new() { Layers = new() { new() { TileCenters = new() { new() { X = 1.25f } } } } } };
            }
            public IReadOnlyList<ModWorldConfigurationExtensionDto> ReadExtensions(string user, string colony, string world) { F.Step("extensions"); return Array.Empty<ModWorldConfigurationExtensionDto>(); }
            public byte[] CaptureSubstrate(ModWorldTileGeometryDto? geometry, out string? failure) { F.Step("substrate"); failure = null; return new byte[] { 1 }; }
            public ClientStorytellerBaseline Capture() { F.Step("story"); return new("Cassandra", "Custom", "<difficulty><threatScale>1.37</threatScale></difficulty>"); }
            public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline) => throw new NotSupportedException();
            public void LockCommitment() => throw new NotSupportedException();
        }
    }
}
