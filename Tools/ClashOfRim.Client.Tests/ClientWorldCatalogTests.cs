using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

internal static class ClientWorldCatalogTests
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
                foreach (bool full in new[] { false, true })
                    foreach (bool inline in new[] { false, true }) { await Success(full, inline); count++; }
                await Freeze(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "login", "generation", "game", "world", "snapshot" })
                    foreach (string phase in new[] { "refreshing", "prepare", "read", "queue", "compatibility", "admin", "remember", "features", "factions", "roads", "extensions", "objects", "story-prepare", "story-apply", "sites", "summary", "success-text" })
                    { await Ownership(change, phase); count++; }
                foreach (string phase in new[] { "prepare", "read" })
                    foreach (string fault in new[] { "network", "null", "missing-result", "rejected", "exception", "canceled", "canceled-result" })
                    { await Failure(phase, fault); count++; }
                foreach (string phase in new[] { "queue", "compatibility", "admin", "remember", "features", "factions", "roads", "extensions", "objects", "story-prepare", "story-apply", "sites", "summary" })
                { await CallbackFailure(phase); count++; }
                foreach (string phase in new[] { "prepare", "read" })
                    foreach (bool oldFirst in new[] { false, true })
                        foreach (bool oldFails in new[] { false, true }) { await Replacement(phase, oldFirst, oldFails); count++; }
                foreach (string phase in new[] { "refreshing", "compatibility", "admin", "remember", "features", "factions", "roads", "extensions", "objects", "story-prepare", "story-apply", "sites", "summary", "success-text" })
                { await Reentry(phase); count++; }
                foreach (string missing in new[] { "server", "user", "colony", "auth" }) { await MissingIdentity(missing); count++; }
                await MissingConfiguration(); count++;
                await NoConfiguration(); count++;
                await QueueReplacement(); count++;
                await Discard(); count++;
                Runtime(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: world catalog refresh ({count} scenarios)");
    }
    private static async Task Success(bool full, bool inline)
    {
        var f = new Fixture(); if (inline) f.Prepared.WorldConfiguration = Config("inline");
        // Even a nonempty faction list does not prove inline data includes the full baseline.
        var task = f.Module.RefreshAsync("手动 İ", full); await f.Ports.PumpUntil(task);
        Check(f.Calls.Count(c => c.Phase == "read") == (full || !inline ? 1 : 0), "Wrong full/catalog read policy.");
        Check(f.Calls.Where(c => c.Phase == "read").All(c => c.Full == full), "Wrong generation flag.");
        Check(f.Calls.All(c => c.Context.UserId == "alice" && c.Context.ColonyId == "colony" && c.Context.AuthToken == "auth" && c.Context.CurrentSnapshotId == "base"), "Refresh borrowed assigned colony or lost captured context.");
        Check(f.Trace.Contains("features") == full && f.Trace.Contains("factions") == full && f.Trace.Contains("roads") == full && f.Trace.Contains("objects") == full, "Catalog-only projection rebuilt world structures.");
        Check(f.Trace.Contains("story-apply") && f.Applied.Single()?.WorldConfigurationId == (inline && !full ? "inline" : "read"), "Wrong configuration/storyteller published.");
        Check(f.Trace.Where(p => p is "compatibility" or "admin" or "remember" or "extensions" or "story-apply" or "sites" or "summary")
            .SequenceEqual(new[] { "compatibility", "admin", "remember", "extensions", "story-apply", "sites", "summary" }), "Publication order changed.");
        Check(f.Statuses.Last().Contains("StatusSucceeded") && !f.Module.Busy && f.Diagnostics.Count == 0, "Refresh did not complete cleanly.");
        f.Ports.Snapshot = "newest"; await f.Ports.PumpUntil(f.Module.RefreshAsync("again"));
        Check(f.Calls.Last().Context.CurrentSnapshotId == "newest", "Tracker reused its first snapshot as request context.");
    }
    private static async Task Freeze()
    {
        var f = new Fixture(); var task = f.Module.RefreshAsync("freeze", true); var queued = await f.Ports.Next();
        Check(f.Applied.Count == 0 && !f.Trace.Contains("compatibility") && !f.Trace.Contains("admin"), "Accepted domain effects ran before game publication.");
        f.Prepared.ServerCompatibilityManifestJson = "mutated"; f.Prepared.IsAdministrator = false;
        f.Read.WorldConfiguration!.Extensions[0].PayloadJson = "mutated";
        f.Read.WorldConfiguration!.DifficultyValuesXml = "mutated";
        f.Read.WorldConfiguration.PlayerColonySites.Clear(); f.Read.WorldConfiguration.Factions.Clear();
        queued(); queued(); await task;
        Check(f.Manifests.Single() == "manifest" && f.Administrators.Single(), "Prepare response remained mutable.");
        Check(f.Applied.Single()!.Extensions[0].PayloadJson == "{\"pollution\":0.25}" && f.Applied.Single()!.PlayerColonySites.Count == 1
            && f.Baselines.Single().ValuesXml == "<difficulty><threatScale>1.37</threatScale></difficulty>", "Read response remained mutable or queue published twice.");
    }
    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture(); bool changed = false;
        f.Hook = point => { if (point == phase && !changed) { changed = true; f.Change(change); } };
        await f.Ports.PumpUntil(f.Module.RefreshAsync("owner", true));
        Check(changed, "Missed boundary: " + phase);
        Check(f.Statuses.Any(s => s.Contains("StatusSucceeded")) == (change == "snapshot"), $"Stale success: {change}/{phase}");
        Check(f.Diagnostics.Count == 0 && !f.Module.Busy, $"Stale failure or unreleased request: {change}/{phase}");
        if (change != "snapshot")
        {
            var trace = f.Trace.ToArray(); int index = Array.IndexOf(trace, phase);
            Check(index == trace.Length - 1, $"Old workflow continued after {change}/{phase}: {string.Join(",", trace.Skip(index))}");
        }
    }
    private static async Task Failure(string phase, string fault)
    {
        var f = new Fixture { FaultPhase = phase, Fault = fault };
        await f.Ports.PumpUntil(f.Module.RefreshAsync("failure", true));
        Check(f.Applied.Count == 0 && f.Manifests.Count == 0 && f.Administrators.Count == 0 && !f.Module.Busy, "Rejected read changed world/domain state: " + phase + "/" + fault);
        string expected = fault is "missing-result" or "rejected" ? "StatusRejected" : fault is "exception" or "canceled" ? "StatusException" : "StatusFailed";
        Check(f.Statuses.Last().Contains(expected), "Wrong failure status: " + fault);
        Check(f.Diagnostics.Count == (fault == "canceled-result" ? 0 : 1), "Wrong diagnostic policy: " + fault);
        f.Fault = null; f.Prepared = Prepare(); f.Read = Read();
        await f.Ports.PumpUntil(f.Module.RefreshAsync("retry", true));
        Check(f.Applied.Count == 1 && f.Statuses.Last().Contains("StatusSucceeded"), "Failure prevented retry.");
    }
    private static async Task CallbackFailure(string phase)
    {
        var f = new Fixture(); bool thrown = false;
        f.Hook = point => { if (point == phase && !thrown) { thrown = true; throw new InvalidOperationException("expected " + phase); } };
        await f.Ports.PumpUntil(f.Module.RefreshAsync("failure", true), _ => { });
        Check(thrown && f.Statuses.Last().Contains("StatusException") && f.Diagnostics.Count == 1 && !f.Module.Busy, "Application exception lost: " + phase);
        f.Hook = null; int before = f.Applied.Count;
        await f.Ports.PumpUntil(f.Module.RefreshAsync("retry", true));
        Check(f.Applied.Count == before + 1 && f.Statuses.Last().Contains("StatusSucceeded"), "Application failure prevented retry.");
    }
    private static async Task Replacement(string phase, bool oldFirst, bool oldFails)
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Before = async point => { if (point == phase) { entered.TrySetResult(); await release.Task; } };
        if (oldFails) { f.FaultPhase = phase; f.Fault = "exception"; }
        var old = f.Module.RefreshAsync("old", true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Before = null; f.Fault = null; f.Prepared = Prepare(); f.Read = Read("new");
        var latest = f.Module.RefreshAsync("new", true); var queued = await f.Ports.Next();
        if (oldFirst) { release.SetResult(); await old; Check(f.Module.Busy, "Old completion released the replacement."); }
        queued(); await latest;
        if (!oldFirst) { release.SetResult(); await old; }
        Check(f.Applied.Single()?.WorldConfigurationId == "new" && f.Diagnostics.Count == 0 && f.Statuses.Last().Contains("StatusSucceeded"), "Old network result/exception crossed replacement.");
    }
    private static async Task Reentry(string phase)
    {
        var f = new Fixture(); Task? next = null; bool changed = false; int boundary = -1;
        f.Hook = point =>
        {
            if (point != phase || changed) return;
            changed = true; boundary = f.Trace.Count;
            f.Prepared = Prepare(); f.Read = Read("replacement");
            next = f.Module.RefreshAsync("replacement", true);
        };
        await f.Ports.PumpUntil(f.Module.RefreshAsync("first", true));
        Check(next is not null, "Reentry stage not reached: " + phase);
        await f.Ports.PumpUntil(next!);
        var trace = f.Trace.ToArray(); int replacementStart = Array.IndexOf(trace, "refreshing", boundary);
        // Every remaining effect belongs to the new request; the old one stops at its callback boundary.
        var suffix = trace.Skip(replacementStart).Where(p => p != "prepare" && p != "read" && p != "queue").ToArray();
        Check(suffix.SequenceEqual(new[] { "refreshing", "compatibility", "admin", "remember", "features", "factions", "roads", "extensions", "objects", "story-prepare", "story-apply", "sites", "summary", "success-text" }), "Reentrant old stages continued: " + phase + " / " + string.Join(",", suffix));
        Check(f.Statuses.Count(s => s.Contains("StatusSucceeded")) == 1 && f.Applied.Last()?.WorldConfigurationId == "replacement" && !f.Module.Busy, "Reentry lost new request.");
    }
    private static async Task QueueReplacement()
    {
        var f = new Fixture(); var old = f.Module.RefreshAsync("old", true); var oldQueue = await f.Ports.Next();
        f.Read = Read("new"); var next = f.Module.RefreshAsync("new", true); var nextQueue = await f.Ports.Next();
        oldQueue(); oldQueue(); await old;
        Check(f.Module.Busy && f.Applied.Count == 0, "Old queued callback released/published over new read.");
        nextQueue(); nextQueue(); await next;
        Check(f.Applied.Single()?.WorldConfigurationId == "new", "Latest queued response not exact.");
    }
    private static async Task MissingIdentity(string missing)
    {
        var f = new Fixture();
        switch (missing) { case "server": f.Ports.Server = ""; break; case "user": f.Ports.User = " "; break; case "colony": f.Ports.Colony = ""; break; default: f.Ports.Auth = " "; break; }
        await f.Module.RefreshAsync("disconnected", true);
        Check(f.Calls.Count == 0 && !f.Module.Busy && f.Statuses.Single().Contains("StatusNotConnected"), "Missing identity sent a request.");
    }
    private static async Task MissingConfiguration()
    {
        var f = new Fixture(); f.Read.WorldConfiguration = null;
        await f.Ports.PumpUntil(f.Module.RefreshAsync("missing", true));
        Check(f.Applied.Count == 0 && f.Administrators.Count == 0 && f.Manifests.Count == 0 && f.Statuses.Last().Contains("StatusFailed"), "Missing configured-world payload cleared existing projection.");
    }
    private static async Task NoConfiguration()
    {
        var f = new Fixture(); f.Prepared.WorldConfigured = false; f.Prepared.WorldConfiguration = Config("inconsistent");
        await f.Ports.PumpUntil(f.Module.RefreshAsync("unconfigured", true));
        Check(f.Calls.Count == 1 && f.Applied.Count == 1 && f.Applied[0] is null && !f.Trace.Contains("extensions")
            && f.Manifests.Count == 1 && f.Statuses.Last().Contains("StatusNoConfiguration"), "Unconfigured world applied an inline catalog or failed to clear sites.");
    }
    private static async Task Discard()
    {
        var f = new Fixture { DiscardQueue = true };
        await f.Ports.PumpUntil(f.Module.RefreshAsync("discard", true));
        Check(!f.Module.Busy && f.Applied.Count == 0 && f.Diagnostics.Count == 0, "Discard stranded/published request.");
    }
    private static void Runtime()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        try
        {
            Current.Game = null; Current.CreatingWorld = null;
            var runtime = new ClientWorldCatalogRuntime(); var menu = runtime.Bind(); Check(menu.IsCurrent, "Stable menu binding failed.");
            Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); Current.Game.World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            Check(!menu.IsCurrent, "Menu refresh borrowed loaded world.");
            var owner = Current.Game; var world = owner.World; var bound = runtime.Bind(); Check(bound.IsCurrent, "World binding failed.");
            owner.World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World)); Check(!bound.IsCurrent, "World replacement was accepted.");
            owner.World = world; Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); Current.Game.World = world;
            Check(!bound.IsCurrent, "New game with same world was accepted.");
        }
        finally { Current.Game = game; Current.CreatingWorld = creating; }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static ModPrepareWorldSessionResponseDto Prepare() => new() { Result = new() { Accepted = true }, WorldConfigured = true, AssignedColonyId = "do-not-reassign", IsAdministrator = true, ServerCompatibilityManifestJson = "manifest" };
    private static ModGetWorldConfigurationResponseDto Read(string id = "read") => new() { Result = new() { Accepted = true }, WorldConfiguration = Config(id) };
    private static ModWorldConfigurationDto Config(string id) => new()
    {
        WorldConfigurationId = id, SeedString = "种子 İ", PlanetCoverage = "0.3", StorytellerDefName = "Cassandra", DifficultyDefName = "Custom",
        DifficultyValuesXml = "<difficulty><threatScale>1.37</threatScale></difficulty>", FactionDefNames = new() { "Tribe" },
        PlayerColonySites = new() { new() { UserId = "alice", ColonyId = "colony", Tile = 1, TileLayerId = 7 } },
        Extensions = new() { new() { ProviderId = "pollution", PayloadJson = "{\"pollution\":0.25}" } }
    };
    private sealed class Fixture : IClientWorldCatalogRuntime, IClientWorldCatalogPresentation, IClientWorldPreparationTransport,
        IClientWorldConfigurationRuntime, IClientWorldConfigurationStore, IClientStorytellerRuntime, IClientWorldFeatureCatalogTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldCatalog Module;
        internal object World = new();
        internal readonly ConcurrentQueue<string> Trace = new(), Statuses = new(), Diagnostics = new();
        internal readonly ConcurrentQueue<(string Phase, ClashOfRimClientNetworkContext Context, bool Full)> Calls = new();
        internal readonly List<string?> Manifests = new(); internal readonly List<bool> Administrators = new();
        internal readonly List<ModWorldConfigurationDto?> Applied = new(); internal readonly List<ClientStorytellerBaseline> Baselines = new();
        internal ModPrepareWorldSessionResponseDto Prepared = Prepare(); internal ModGetWorldConfigurationResponseDto Read = ClientWorldCatalogTests.Read();
        internal Action<string>? Hook; internal Func<string, Task>? Before; internal string? FaultPhase, Fault; internal bool DiscardQueue;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            var storyteller = new ClientStorytellerConfiguration(Session, new(Session), this);
            Module = new(Session, this, this, this, new ClientWorldConfiguration(Session, this, this, storyteller, new(Session, this)));
            Module.CompatibilityAccepted += manifest => { Manifests.Add(manifest); Step("compatibility"); };
            Module.AdministratorObserved += admin => { Administrators.Add(admin); Step("admin"); };
            Module.Applied += config => { Applied.Add(config); Step("sites"); };
        }
        internal void Step(string point) { Trace.Enqueue(point); Hook?.Invoke(point); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break; case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); break;
            }
        }
        public string Text(string key, params (string Name, object? Value)[] arguments)
        {
            if (key.EndsWith("StatusRefreshing", StringComparison.Ordinal)) Step("refreshing");
            if (key.EndsWith("StatusSucceeded", StringComparison.Ordinal)) Step("success-text");
            return key + " " + string.Join(";", arguments.Select(a => a.Value));
        }
        public void Status(string message) => Statuses.Enqueue(message);
        public void Diagnose(string message) => Diagnostics.Enqueue(message);
        public string Summary(ModWorldConfigurationDto configuration) { Step("summary"); return configuration.Extensions[0].PayloadJson!; }
        public void Remember(string worldId) => Step("remember");
        IClientWorldCatalogSession IClientWorldCatalogRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientWorldConfigurationSession IClientWorldConfigurationRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        public Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => throw new InvalidOperationException("Catalog repeated entry handshake.");
        public Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token)
        { Check(!createAccount, "Refresh attempted registration."); var reply = Prepared; return Network("prepare", context, false, reply, result => reply.Result = result); }
        public Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, bool generation, CancellationToken token)
        { var reply = Read; return Network("read", context, generation, reply, result => reply.Result = result); }
        private async Task<ClashOfRimClientNetworkResult<T>> Network<T>(string phase, ClashOfRimClientNetworkContext context, bool full, T reply, Action<ModProtocolResponseDto?> result) where T : class
        {
            Calls.Enqueue((phase, context, full)); string? fault = FaultPhase == phase ? Fault : null;
            Step(phase); if (Before is not null) await Before(phase);
            switch (fault)
            {
                case "network": return ClashOfRimClientNetworkResult<T>.Failed("network", "expected");
                case "canceled-result": return ClashOfRimClientNetworkResult<T>.Failed(nameof(TaskCanceledException), "expected");
                case "null": return ClashOfRimClientNetworkResult<T>.Ok(null!);
                case "missing-result": result(null); break;
                case "rejected": result(new() { Accepted = false, ErrorCode = 17, Message = "expected" }); break;
                case "exception": throw new InvalidOperationException("expected");
                case "canceled": throw new TaskCanceledException("expected");
            }
            return ClashOfRimClientNetworkResult<T>.Ok(reply);
        }
        public Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(ClashOfRimClientNetworkContext context, string language, string world, IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token)
            => throw new InvalidOperationException("No generated names should be submitted by this fixture.");
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldCatalogSession, IClientWorldConfigurationSession, IClientStorytellerSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool IsPlaying => true;
            public void Enqueue(Action action, Action discard) { F.Step("queue"); F.Ports.Enqueue(() => { if (F.DiscardQueue) discard(); else action(); }); }
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) { F.Step("features"); return null; }
            public void ApplyFactions(ModWorldConfigurationDto configuration) => F.Step("factions");
            public void ApplyRoads(ModWorldConfigurationDto configuration) => F.Step("roads");
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState)
            { Check(userId == F.Ports.User && !worldState, "Wrong extension context."); F.Step("extensions"); }
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => F.Step("objects");
            public ClientStorytellerBaseline Capture() => throw new NotSupportedException();
            public IClientStorytellerChange Prepare(ClientStorytellerBaseline baseline) { F.Step("story-prepare"); return new Story(F, baseline); }
            public void LockCommitment() => throw new NotSupportedException();
        }
        private sealed record Story(Fixture F, ClientStorytellerBaseline Baseline) : IClientStorytellerChange
        { public void Apply() { F.Baselines.Add(Baseline); F.Step("story-apply"); } }
    }
}
