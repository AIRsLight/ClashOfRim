using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientWorldGenerationTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Success(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "game", "generation", "snapshot", "plan" })
                    foreach (string stage in new[] { "close", "generate", "publish", "substrate", "names", "factions", "roads", "extensions", "objects", "completion", "status", "next" })
                    { Ownership(change, stage); count++; }
                foreach (string stage in new[] { "close", "queue", "generate", "publish", "substrate", "names", "factions", "roads", "extensions", "objects", "completion" })
                { Failure(stage); count++; }
                foreach (string value in new[] { "", "bad", "NaN", "Infinity", "-Infinity", "0", "-0.3", "1.01", "0,3" }) { Coverage(value); count++; }
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "game", "generation", "snapshot", "plan" })
                    foreach (bool generated in new[] { false, true }) { Deferred(change, generated); count++; }
                Projection(); count++;
            }
            Catalogs().GetAwaiter().GetResult(); count++;
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: world generation/application ({count} scenarios)");
    }
    private static void Success()
    {
        var f = new Fixture(); Check(!f.Generation.TryStart(f.Page), "Ordinary generation intercepted.");
        f.Prepare(false); Check(f.Generation.TryStart(f.Page) && f.Queued.Count == 0 && f.Page.Open, "Pending download fell through or closed setup.");
        f.Attach();
        Check(f.Generation.TryStart(f.Page) && !f.Page.Open && f.Queued.Count == 1 && !f.Setup.HasConfiguration, "Ready plan not consumed exactly once.");
        Check(f.Generation.TryStart(f.Page) && f.Queued.Count == 1, "Busy generation queued twice.");
        var run = f.Queued.Dequeue(); run(); run();
        Check(f.Completed.Count == 1 && f.Page.Advanced == 0, "Generation did not await UI completion/ran twice.");
        var finish = f.Completed.Dequeue(); finish(); finish();
        Check(f.Page.Advanced == 1 && f.Successes == 1 && f.Failures == 0 && f.Created.Single().Committed, "Completion not exact.");
        Check(string.Join(",", f.Trace) == "close,queue,generate,publish,substrate,remember,names,factions,roads,extensions,objects,completion,status,next", "Generation order changed: " + string.Join(",", f.Trace));
    }
    private static void Ownership(string change, string stage)
    {
        var f = new Fixture(); f.Prepare(); bool changed = false;
        f.Hook = point => { if (point == stage && !changed) { changed = true; f.Change(change); } };
        f.Generation.TryStart(f.Page); f.Pump();
        Check(changed, "Test missed ownership stage: " + stage);
        bool advances = change == "snapshot" || stage == "next";
        Check(f.Page.Advanced == (advances ? 1 : 0), $"Old completion advanced: {change}/{stage}.");
        Check(f.Failures == 0, $"Expired generation displayed failure: {change}/{stage}.");
        if (change == "plan") Check(f.Setup.Configuration?.SeedString == "replacement", "Old cleanup cleared replacement plan.");
    }
    private static void Deferred(string change, bool generated)
    {
        var f = new Fixture(); f.Prepare(); f.Generation.TryStart(f.Page);
        if (generated) f.Queued.Dequeue()();
        f.Change(change); f.Pump();
        Check(f.Page.Advanced == (change == "snapshot" ? 1 : 0) && f.Failures == 0, $"Deferred callback crossed ownership: {change}/{generated}.");
        if (change == "plan") Check(f.Setup.Configuration?.SeedString == "replacement", "Discard cleared later setup.");
    }
    private static void Failure(string stage)
    {
        var f = new Fixture(); f.Prepare();
        f.Hook = point => { if (point == stage) throw new InvalidOperationException("expected " + stage); };
        f.Generation.TryStart(f.Page); f.Pump();
        Check(f.Failures == 1 && f.Page.Open && f.Page.Advanced == 0 && f.Setup.HasConfiguration, "Failure did not restore owned setup: " + stage);
        Check(ReferenceEquals(f.World, f.InitialWorld), "Failed generation left a partial world: " + stage);
        f.Hook = null; f.Generation.TryStart(f.Page); f.Pump();
        Check(f.Page.Advanced == 1 && f.Successes == 1 && !f.Setup.HasConfiguration, "Failure prevented retry: " + stage);
    }
    private static void Coverage(string value)
    {
        var f = new Fixture(); f.Prepare(coverage: value); f.Generation.TryStart(f.Page); f.Pump();
        Check(f.Failures == 1 && f.Setup.HasConfiguration && f.Page.Open && f.Created.Count == 0, "Invalid coverage reached generation: " + value);
    }
    private static void Projection()
    {
        var f = new Fixture(); var c = Config();
        Check(f.Configuration.Apply(c), "Full projection failed.");
        Check(string.Join(",", f.Trace) == "remember,features,factions,roads,extensions,objects", "Full projection order changed.");
        f.Trace.Clear(); Check(f.Configuration.ApplyCatalog(c), "Catalog projection failed.");
        Check(string.Join(",", f.Trace) == "remember,extensions", "Catalog refreshed world structures.");
        f.Trace.Clear(); f.Hook = stage => { if (stage == "extensions") f.Change("user"); };
        Check(!f.Configuration.Apply(c) && !f.Trace.Contains("objects"), "Projection borrowed a new identity after extension code.");
    }
    private static async Task Catalogs()
    {
        var f = new Fixture(); var names = new ClientWorldFeatureNames("fr-FR", new[] { new ModWorldFeatureDto { DefName = "Mountain", Label = "Montagne", DrawCenterX = 1.37f } });
        var pending = new Queue<TaskCompletionSource<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>>>();
        f.Send = (_, _, _, _) => { var next = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously); pending.Enqueue(next); return next.Task; };
        var first = f.Catalogs.SubmitAsync(" world ", names);
        names.Features[0].Label = "modified";
        await f.Catalogs.SubmitAsync("world", names with { Language = " FR-fr " });
        Check(f.Sent.Count == 1 && f.Sent[0].Features[0].Label == "Montagne" && f.Sent[0].World == "world", "Catalog did not freeze input/deduplicate in flight.");
        pending.Dequeue().SetResult(ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>.Failed("expected", "expected")); await first;
        var retry = f.Catalogs.SubmitAsync("world", names);
        pending.Dequeue().SetResult(Accepted(false)); await retry;
        var accepted = f.Catalogs.SubmitAsync("world", names);
        pending.Dequeue().SetResult(Accepted(true)); await accepted;
        await f.Catalogs.SubmitAsync("world", names);
        Check(f.Sent.Count == 3, "Failure/rejection prevented retry or accepted receipt repeated.");
        // A delimiter in either part cannot alias a distinct (world, language) pair.
        var left = f.Catalogs.SubmitAsync("a\u001fb", names with { Language = "c" });
        var right = f.Catalogs.SubmitAsync("a", names with { Language = "b\u001fc" });
        Check(pending.Count == 2, "Catalog keys collide.");
        pending.Dequeue().SetResult(Accepted(true)); pending.Dequeue().SetResult(Accepted(true)); await Task.WhenAll(left, right);
        var old = f.Catalogs.SubmitAsync("stale", names); var oldResult = pending.Dequeue();
        f.Change("server");
        var replacement = f.Catalogs.SubmitAsync("stale", names); var newResult = pending.Dequeue();
        oldResult.SetResult(Accepted(true)); await old;
        int sent = f.Sent.Count; await f.Catalogs.SubmitAsync("stale", names);
        Check(f.Sent.Count == sent, "Old completion removed new request.");
        newResult.SetException(new InvalidOperationException("expected transport")); await replacement;
        var afterException = f.Catalogs.SubmitAsync("stale", names); pending.Dequeue().SetResult(Accepted(true)); await afterException;
        Check(f.Sent.Last().Server == "http://other", "Catalog borrowed old server context.");
    }
    private static ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto> Accepted(bool accepted)
        => ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>.Ok(new() { Result = new() { Accepted = accepted }, Accepted = accepted });
    private static ModWorldConfigurationDto Config(string seed = "seed", string coverage = "0.3") => new()
    { WorldConfigurationId = "world", SeedString = seed, PlanetCoverage = coverage, GameLanguage = "en-US" };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Page : IClientWorldGenerationPage
    {
        private readonly Fixture f;
        internal bool Open = true; internal int Advanced;
        internal Page(Fixture f) => this.f = f;
        public bool IsCurrent => Open;
        public void Close() { f.Step("close"); Open = false; }
        public void Reopen() => Open = true;
        public void Continue(Func<bool> current) { if (current()) { Advanced++; f.Step("next"); } }
    }
    private sealed class Fixture : IClientWorldGenerationRuntime, IClientWorldConfigurationRuntime, IClientWorldConfigurationStore,
        IClientWorldGenerationPresentation, IClientStorytellerRuntime, IClientWorldFeatureCatalogTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldSetupState Setup;
        internal readonly ClientWorldFeatureCatalogs Catalogs;
        internal readonly ClientWorldConfiguration Configuration;
        internal readonly ClientWorldGeneration Generation;
        internal readonly Page Page;
        internal readonly List<string> Trace = new();
        internal readonly Queue<Action> Queued = new(), Completed = new();
        internal readonly List<BuiltWorld> Created = new();
        internal readonly object InitialWorld = new(); internal object World;
        internal Action<string>? Hook;
        internal int Successes, Failures;
        internal readonly List<(string Server, string World, IReadOnlyList<ModWorldFeatureDto> Features)> Sent = new();
        internal Func<ClashOfRimClientNetworkContext, string, string, IReadOnlyList<ModWorldFeatureDto>, Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>>>? Send;
        internal Fixture()
        {
            ClientSessionWork.Reset(); World = InitialWorld; Session = new(Ports, Ports, new(Ports)); Setup = new(Session); Page = new(this);
            var storyteller = new ClientStorytellerConfiguration(Session, Setup, this); Catalogs = new(Session, this);
            Configuration = new(Session, this, this, storyteller, Catalogs); Generation = new(Session, Setup, Configuration, this, this);
        }
        internal void Step(string name) { Trace.Add(name); Hook?.Invoke(name); }
        internal void Prepare(bool ready = true, string seed = "seed", string coverage = "0.3")
        { Setup.Prepare(new() { Result = new() { Accepted = true }, WorldConfigured = true, WorldConfiguration = Config(seed, coverage) }); if (ready) Attach(); }
        internal void Attach() => Setup.AttachSubstrate(Setup.CaptureGeneration()!, new(42, "<grid/>", "<features/>", "<landmarks/>", Array.Empty<byte>()));
        internal void Pump() { while (Queued.TryDequeue(out var next)) next(); while (Completed.TryDequeue(out var finish)) finish(); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); break;
                case "plan": Prepare(seed: "replacement"); break;
            }
        }
        public void Remember(string world) => Step("remember");
        public void Generated(string seed) { Successes++; Step("status"); }
        public void Failed(Exception exception) => Failures++;
        IClientWorldGenerationSession IClientWorldGenerationRuntime.Bind() => new GenerationSession(this, Ports.GameOwner, World);
        IClientWorldConfigurationSession IClientWorldConfigurationRuntime.Bind() => new ConfigurationSession(this, Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new StorytellerSession();
        public Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(ClashOfRimClientNetworkContext context, string language, string world, IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token)
        { Sent.Add((context.ServerBaseUrl, world, features)); return Send?.Invoke(context, language, world, features) ?? Task.FromResult(Accepted(true)); }
        private sealed class StorytellerSession : IClientStorytellerSession
        {
            public bool IsCurrent => true; public bool IsPlaying => false;
            public ClientStorytellerBaseline Capture() => new(null, null, null);
            public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline) => null;
            public void LockCommitment() { }
        }
        private sealed record ConfigurationSession(Fixture F, object Game, object World) : IClientWorldConfigurationSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) { F.Step("features"); return null; }
            public ClientWorldFeatureNames? CaptureLocalizedFeatures(ModWorldConfigurationDto configuration) { F.Step("names"); return null; }
            public void ApplyFactions(ModWorldConfigurationDto configuration) => F.Step("factions");
            public void ApplyRoads(ModWorldConfigurationDto configuration) => F.Step("roads");
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState) => F.Step("extensions");
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => F.Step("objects");
        }
        internal sealed class GenerationSession : IClientWorldGenerationSession
        {
            internal readonly Fixture F; internal readonly object Game; internal object World;
            internal GenerationSession(Fixture f, object game, object world) { F = f; Game = game; World = world; }
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public void Queue(Action generate) { F.Step("queue"); F.Queued.Enqueue(generate); }
            public void WhenFinished(Action complete) => F.Completed.Enqueue(complete);
            public IClientGeneratedWorld Generate(ClientWorldGenerationInput input, Func<bool> current)
            { F.Step("generate"); var result = new BuiltWorld(this); F.Created.Add(result); return result; }
        }
        internal sealed class BuiltWorld : IClientGeneratedWorld
        {
            private readonly GenerationSession owner; private readonly object previous, value = new();
            private bool published, disposed; internal bool Committed;
            internal BuiltWorld(GenerationSession owner) { this.owner = owner; previous = owner.World; }
            public void Publish() { owner.F.World = owner.World = value; published = true; owner.F.Step("publish"); }
            public void ApplySubstrate(WorldSubstratePackage substrate, ModWorldConfigurationDto configuration) => owner.F.Step("substrate");
            public void Complete(Func<bool> current) { owner.F.Step("completion"); Committed = current(); }
            public void Dispose() { if (disposed) return; disposed = true; if (!Committed && published && owner.IsCurrent) owner.F.World = owner.World = previous; }
        }
    }
}
