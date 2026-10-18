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
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldWritesTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        int count = 0; var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Admission(); count++;
                foreach (var kind in Enum.GetValues<ClientWorldWriteKind>())
                {
                    await Success(kind); count++;
                    await DelayedPublication(kind); count++;
                    if (kind == ClientWorldWriteKind.Storyteller)
                        foreach (string phase in new[] { "source", "metadata", "upload", "queue", "apply-extensions" })
                        { await Ownership(kind, "observation", phase); count++; }
                    foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "permission", "snapshot" })
                        foreach (string phase in new[] { "source", "capture-story", "source-extensions", "substrate", "metadata", "upload", "queue", "apply-extensions", "prepare-story", "apply-story", "sites", "success-text", "message" })
                        { await Ownership(kind, change, phase); count++; }
                    foreach (string phase in new[] { "metadata", "upload" })
                        foreach (string fault in new[] { "network", "missing-result", "rejected", "exception", "canceled" })
                        { await Failure(kind, phase, fault); count++; }
                    foreach (string phase in new[] { "source", "capture-story", "source-extensions", "substrate", "apply-extensions", "prepare-story", "apply-story", "sites", "success-text" })
                    { await CallbackFailure(kind, phase); count++; }
                    foreach (string phase in new[] { "metadata", "upload" })
                        foreach (bool oldFirst in new[] { false, true }) { await Replacement(kind, phase, oldFirst); count++; }
                    await Busy(kind); count++; await Queue(kind); count++;
                    await MissingResponseConfiguration(kind); count++;
                    await CaptureFailure(kind); count++;
                }
                foreach (string gate in new[] { "unchanged", "no-snapshot", "no-session", "observing", "not-admin", "missing-world", "busy-scribe" })
                { await StorytellerGate(gate); count++; }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: administrator admission and world writes ({count} scenarios)");
    }
    private static async Task DelayedPublication(ClientWorldWriteKind kind)
    {
        foreach (bool newerSource in new[] { false, true })
        {
            var f = new Fixture(kind); var delay = new ClientWorldPublicationTestPorts.DelayedReply();
            f.Accepted = Config(); f.Accepted.PlayerColonySites = ClientWorldPublicationTests.Data("old").PlayerColonySites;
            f.Before = phase => phase == "metadata" ? delay.Wait() : Task.CompletedTask;
            var task = f.Start();
            await delay.Complete(f.Ports, task, () =>
            {
                f.Publication.Appearance.SaveCurrent(ClientWorldPublicationTests.Selection("local"));
                if (newerSource) f.Publication.Flow.Publish(f.Publication.Flow.Capture(), ClientWorldPublicationTests.Data("new", f.WorldId),
                    ClientWorldProjection.Sites, () => true, ("newer-source", ClientWorldPublicationTests.Markers("new")));
            });
            Check(f.Publication.Appearance.Read().ColorHex == (newerSource ? "new" : "local") && f.Successes == 1, "Delayed write replaced newer preference or lost successful completion.");
            if (newerSource) Check(f.Publication.Sites.IsBlocked(4, 7, out var site) && site!.Label == "new", "Old write replaced newer sites.");
        }
    }
    private static void Admission()
    {
        var f = new Fixture(ClientWorldWriteKind.Baseline); f.Access.SetAdministrator(false);
        Check(f.Access.Begin(out var denial) is null && denial == "ClashOfRim.Admin.NotAdministrator", "Non-admin admitted.");
        f.Access.SetAdministrator(true); f.Ports.Auth = "";
        Check(f.Access.Begin(out denial) is null && denial == "ClashOfRim.Admin.NotConnected", "Disconnected admin admitted.");
        f.Ports.Auth = "auth"; f.Access.SetAdministrator(true);
        var old = f.Access.Begin(out denial)!; Check(denial is null && f.Access.Busy && old.Context.CurrentSnapshotId == "base", "Admission did not capture context.");
        Check(f.Access.Begin(out denial) is null && denial is null, "Concurrent admin requests admitted.");
        f.Access.SetAdministrator(false); f.Access.SetAdministrator(true);
        f.Ports.Snapshot = "new"; var next = f.Access.Begin(out denial)!;
        Check(next.Context.CurrentSnapshotId == "new" && !f.Access.TryApply(old, () => throw new Exception()), "Regrant revived old ticket/context.");
        f.Access.End(old); Check(f.Access.Busy, "Old completion released new request."); f.Access.End(next);
        bool current = true; old = f.Access.Begin(out denial, () => current)!; current = false;
        Check(!f.Access.Busy, "Lost world did not release admission."); next = f.Access.Begin(out denial)!; f.Access.End(old);
        Check(f.Access.Busy, "Expired world completion released new admission."); f.Access.End(next);
        f.Session.PublishLogin(new() { AuthenticatedUserId = f.Ports.User, AuthToken = f.Ports.Auth, SessionId = "replacement" });
        Check(!f.Access.IsAdministrator, "Same-credentials new login inherited administrator authority.");
    }
    private static async Task Success(ClientWorldWriteKind kind)
    {
        var f = new Fixture(kind); var work = f.Start(); var queue = await f.Ports.Next();
        Check(f.Access.Busy && f.Applied.Count == 0 && !f.Statuses.Any(s => s.Contains("WorldBaselineUpdated") || s.Contains("AdminSynced")), "Success published before queued application.");
        queue(); queue(); await work;
        Check(f.Applied.Count == 1 && f.Successes == 1 && f.Markers == (kind == ClientWorldWriteKind.Baseline ? 1 : 0) && !f.Access.Busy, "Completion not exact.");
        Check(f.Trace.Contains("apply-features") == (kind == ClientWorldWriteKind.Baseline) && f.Trace.Contains("apply-story"), "Wrong application mode.");
        Check(f.Calls.SequenceEqual(new[] { "metadata", "upload" }) && f.Contexts.All(c => c.UserId == "alice" && c.ColonyId == "colony" && c.CurrentSnapshotId == "base"), "Write stages/context changed.");
        Check(f.Sent!.PlanetCoverage == "0.3" && f.Sent.DifficultyValuesXml == Baseline.ValuesXml && f.Sent.Extensions[0].PayloadJson == "{\"value\":0.25}", "Culture changed baseline payload.");
    }
    private static async Task Ownership(ClientWorldWriteKind kind, string change, string phase)
    {
        var f = new Fixture(kind); bool changed = false;
        f.Hook = point => { if (point == phase && !changed) { changed = true; f.Change(change); } };
        await f.Ports.PumpUntil(f.Start());
        Check(changed, $"Missed phase {kind}/{phase}");
        bool allowed = change == "snapshot";
        Check(f.Successes == (allowed || phase == "message" ? 1 : 0), $"Stale success {kind}/{change}/{phase}");
        Check(f.Diagnostics.Count == 0 && !f.Access.Busy, $"Stale exception/admission {kind}/{change}/{phase}");
        if (!allowed)
        {
            var trace = f.Trace.ToArray(); int index = Array.IndexOf(trace, phase);
            Check(index == trace.Length - 1, $"Old stages continued at {kind}/{change}/{phase}: {string.Join(",", trace.Skip(index))}");
        }
    }
    private static async Task Failure(ClientWorldWriteKind kind, string phase, string fault)
    {
        var f = new Fixture(kind) { FaultPhase = phase, Fault = fault };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Successes == 0 && f.Applied.Count == 0 && !f.Access.Busy && f.Statuses.Last().Contains(fault is "exception" or "canceled" ? "Exception" : phase == "metadata" && fault == "rejected" ? "Rejected" : "Failed"), "Failed submission published success: " + phase + "/" + fault);
        f.Fault = null; await f.Ports.PumpUntil(f.Start());
        Check(f.Successes == 1, "Submission failure prevented retry.");
    }
    private static async Task CallbackFailure(ClientWorldWriteKind kind, string phase)
    {
        var f = new Fixture(kind); bool failed = false;
        f.Hook = point => { if (point == phase && !failed) { failed = true; throw new InvalidOperationException("expected " + phase); } };
        await f.Ports.PumpUntil(f.Start(), _ => { });
        Check(failed && f.Successes == 0 && f.Diagnostics.Count == 1 && !f.Access.Busy, "Capture/application exception lost: " + phase);
        f.Hook = null; await f.Ports.PumpUntil(f.Start()); Check(f.Successes == 1, "Callback failure prevented retry.");
    }
    private static async Task Replacement(ClientWorldWriteKind kind, string phase, bool oldFirst)
    {
        var f = new Fixture(kind); var entered = Signal(); var release = Signal();
        f.Before = async point => { if (point == phase) { entered.SetResult(); await release.Task; } };
        var old = f.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.World = new(); f.Before = null; var next = f.Start(); var queue = await f.Ports.Next();
        if (oldFirst) { release.SetResult(); await old; Check(f.Access.Busy, "Old finally released replacement."); }
        queue(); await next;
        if (!oldFirst) { release.SetResult(); await old; }
        Check(f.Successes == 1 && f.Applied.Count == 1 && f.Diagnostics.Count == 0 && !f.Access.Busy, "Old world response crossed replacement.");
    }
    private static async Task Busy(ClientWorldWriteKind kind)
    {
        var f = new Fixture(kind); var held = f.Access.Begin(out _)!;
        await f.Start(); Check(f.Trace.IsEmpty && f.Calls.IsEmpty && f.Access.Busy, "Writer bypassed another administrator request.");
        f.Access.End(held);
        var work = f.Start(); var queue = await f.Ports.Next(); int captures = f.Trace.Count(p => p == "source");
        await f.Start(); Check(f.Trace.Count(p => p == "source") == captures && f.Access.Begin(out _) is null, "Concurrent write/admin request bypassed admission.");
        queue(); await work; Check(!f.Access.Busy, "Completed write retained admission.");
    }
    private static async Task Queue(ClientWorldWriteKind kind)
    {
        var f = new Fixture(kind); var old = f.Start(); var oldQueue = await f.Ports.Next();
        f.World = new(); var next = f.Start(); var newQueue = await f.Ports.Next(); oldQueue(); oldQueue(); await old;
        Check(f.Access.Busy && f.Applied.Count == 0, "Expired queued publication affected next request.");
        newQueue(); await next;
        f.DiscardQueue = true; await f.Ports.PumpUntil(f.Start());
        Check(f.Successes == 1 && !f.Access.Busy, "Discarded write published/stranded admission.");
    }
    private static async Task MissingResponseConfiguration(ClientWorldWriteKind kind)
    {
        var f = new Fixture(kind) { NoAcceptedConfiguration = true };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Successes == 1 && f.Applied.Count == 0 && !f.Trace.Contains("apply-story"), "Absent optional accepted config was fabricated/applied.");
        if (kind == ClientWorldWriteKind.Storyteller) Check(f.Statuses.Last().Contains("Cassandra") && f.Statuses.Last().Contains("Custom"), "Success lost captured storyteller fallback.");
    }
    private static async Task CaptureFailure(ClientWorldWriteKind kind)
    {
        var f = new Fixture(kind) { InvalidSource = true };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Calls.IsEmpty && f.Successes == 0 && f.Rejections == 1 && !f.Access.Busy, "Invalid capture sent a request.");
    }
    private static async Task StorytellerGate(string gate)
    {
        var f = new Fixture(ClientWorldWriteKind.Storyteller); string? signature = null;
        switch (gate)
        {
            case "unchanged": signature = Baseline.Signature; break;
            case "no-snapshot": f.Ports.Snapshot = ""; break;
            case "no-session": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); f.Access.SetAdministrator(true); break;
            case "observing": f.Observing = true; break;
            case "not-admin": f.Access.SetAdministrator(false); break;
            case "missing-world": f.HasWorld = false; break;
            case "busy-scribe": f.CaptureError = "Scribe is busy"; break;
        }
        await f.Ports.PumpUntil(f.Start(signature));
        Check(f.Calls.IsEmpty && f.Successes == 0 && !f.Access.Busy && !f.Trace.Contains("source"), "Storyteller gate did not stop capture: " + gate);
        if (gate == "busy-scribe") Check(f.Rejections == 1, "Scribe capture failure was treated as unchanged preset.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static readonly ClientStorytellerBaseline Baseline = new("Cassandra", "Custom", "<difficulty><threatScale>1.37</threatScale></difficulty>");
    private static ModWorldConfigurationDto Config() => new()
    {
        WorldConfigurationId = "world", SeedString = "种子 İ", PlanetCoverage = "0.3", GameLanguage = "French",
        FactionDefNames = new() { "Tribe" }, Features = new() { new() { DefName = "Island", Label = "île" } },
        StorytellerDefName = Baseline.Storyteller, DifficultyDefName = Baseline.Difficulty, DifficultyValuesXml = Baseline.ValuesXml,
        TileGeometry = new() { Layers = new() { new() { TileCenters = new() { new() { X = 1.25f } } } } }
    };
    private sealed class Fixture : IClientWorldBaselineRuntime, IClientWorldBaselineTransport, IClientStorytellerRuntime,
        IClientWorldConfigurationRuntime, IClientWorldConfigurationStore, IClientWorldFeatureCatalogTransport, IClientWorldWriteRuntime, IClientWorldWritePresentation
    {
        internal readonly EventModuleTestPorts Ports = new(); internal readonly ClientSessionLifecycle Session; internal readonly ClientAdminAccess Access;
        internal readonly ClientWorldWrites Module; internal readonly ClientWorldWriteKind Kind;
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal string WorldId = "world";
        internal object World = new(); internal bool HasWorld = true, Observing, DiscardQueue, InvalidSource, NoAcceptedConfiguration;
        internal readonly ConcurrentQueue<string> Trace = new(), Calls = new(), Statuses = new(), Diagnostics = new();
        internal readonly ConcurrentQueue<ClashOfRimClientNetworkContext> Contexts = new();
        internal readonly List<ModWorldConfigurationDto> Applied = new();
        internal ModWorldConfigurationDto? Accepted;
        internal ModWorldConfigurationDto? Sent; internal int Successes, Rejections, Markers, Captures;
        internal string? FaultPhase, Fault, CaptureError; internal Action<string>? Hook; internal Func<string, Task>? Before;
        internal Fixture(ClientWorldWriteKind kind)
        {
            Kind = kind; ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            Access = new(Session); Access.SetAdministrator(true);
            var storyteller = new ClientStorytellerConfiguration(Session, new(Session), this);
            var submission = new ClientWorldBaselineSubmission(Session, this, this);
            var configuration = new ClientWorldConfiguration(Session, this, this, storyteller, new(Session, this));
            Publication = new(Session, Ports, () => WorldId, () => World, configuration);
            Module = new(Session, Access, new(submission, storyteller), submission, storyteller, Publication.Flow, this, this);
            Module.Applied += configuration => { Applied.Add(configuration); Step("sites"); };
            Module.BaselineUpdated += () => { Markers++; Step("markers"); };
        }
        internal Task Start(string? signature = null) { Captures = 0; return Module.StartAsync(Kind, null, signature); }
        internal void Step(string point) { Trace.Enqueue(point); Hook?.Invoke(point); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break; case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); break; case "permission": Access.SetAdministrator(false); break;
                case "observation": Observing = true; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); Access.SetAdministrator(true); break;
            }
        }
        public bool IsObserving => Observing;
        public void Enqueue(Action action, Action discard) { Step("queue"); Ports.Enqueue(() => { if (DiscardQueue) discard(); else action(); }); }
        public string Text(string key, params (string Name, object? Value)[] args)
        { if (key is "ClashOfRim.Storyteller.AdminSynced" or "ClashOfRim.Admin.WorldBaselineUpdated") Step("success-text"); return key + " " + string.Join(";", args.Select(a => a.Value)); }
        public string CaptureFailure(ClientWorldBaselineCapture.Failure failure) => failure.Detail ?? failure.MessageKey ?? failure.Code;
        public void Status(string text) => Statuses.Enqueue(text);
        public void Diagnose(string message) => Diagnostics.Enqueue(message);
        public void Message(string text, ClientWorldWriteMessage kind)
        { if (kind == ClientWorldWriteMessage.Success) { Successes++; Step("message"); } if (kind == ClientWorldWriteMessage.Reject) Rejections++; }
        public void Remember(string id) { WorldId = id; Step("remember"); }
        IClientWorldBaselineSession IClientWorldBaselineRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientWorldConfigurationSession IClientWorldConfigurationRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        private async Task<string?> Network(string phase, ClashOfRimClientNetworkContext context)
        { Contexts.Enqueue(context); Calls.Enqueue(phase); string? fault = phase == FaultPhase ? Fault : null; Step(phase); if (Before is not null) await Before(phase); return fault; }
        public async Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
        {
            Sent = configuration; string? fault = await Network("metadata", context);
            return Result(fault, new ModSubmitWorldConfigurationResponseDto { Result = Protocol(fault), WorldConfiguration = NoAcceptedConfiguration ? null : Accepted ?? Config() });
        }
        public async Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token)
        { string? fault = await Network("upload", context); return Result(fault, new ModUploadWorldSubstrateResponseDto { Result = Protocol(fault) }); }
        private static ModProtocolResponseDto? Protocol(string? fault) => fault == "missing-result" ? null : new() { Accepted = fault != "rejected", ErrorCode = fault == "rejected" ? 17 : 0, Message = "expected" };
        private static ClashOfRimClientNetworkResult<T> Result<T>(string? fault, T response) => fault switch
        { "exception" => throw new InvalidOperationException("expected"), "canceled" => throw new TaskCanceledException("expected"), "network" => ClashOfRimClientNetworkResult<T>.Failed("network", "expected"), _ => ClashOfRimClientNetworkResult<T>.Ok(response) };
        public Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(ClashOfRimClientNetworkContext context, string language, string world, IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token) => throw new NotSupportedException();
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldBaselineSession, IClientStorytellerSession, IClientWorldConfigurationSession
        {
            public bool IsCurrent => F.HasWorld && ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public string Language => "French"; public bool IsPlaying => true;
            public ModWorldConfigurationDto ReadWorld(string userId, string colonyId, ModColonyAppearanceDto? appearance)
            { F.Step("source"); var value = Config(); if (F.InvalidSource) value.SeedString = ""; return value; }
            public IReadOnlyList<ModWorldConfigurationExtensionDto> ReadExtensions(string userId, string colonyId, string worldId)
            { F.Step("source-extensions"); return new[] { new ModWorldConfigurationExtensionDto { ProviderId = "pollution", PayloadJson = "{\"value\":0.25}" } }; }
            public byte[] CaptureSubstrate(ModWorldTileGeometryDto? geometry, out string? failure) { F.Step("substrate"); failure = null; return new byte[] { 1 }; }
            public ClientStorytellerBaseline Capture()
            { F.Captures++; F.Step(F.Kind == ClientWorldWriteKind.Storyteller && F.Captures == 1 ? "signature" : "capture-story"); return F.InvalidSource ? new(null, null, null) : Baseline with { CaptureFailure = F.CaptureError }; }
            public IClientStorytellerChange Prepare(ClientStorytellerBaseline baseline) { F.Step("prepare-story"); return new StoryChange(F); }
            public void LockCommitment() => throw new NotSupportedException();
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) { F.Step("apply-features"); return null; }
            public void ApplyFactions(ModWorldConfigurationDto configuration) => F.Step("apply-factions");
            public void ApplyRoads(ModWorldConfigurationDto configuration) => F.Step("apply-roads");
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState) => F.Step("apply-extensions");
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => F.Step("apply-objects");
        }
        private sealed record StoryChange(Fixture F) : IClientStorytellerChange { public void Apply() => F.Step("apply-story"); }
    }
}
