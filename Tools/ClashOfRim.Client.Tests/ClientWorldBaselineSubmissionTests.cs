using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

internal static class ClientWorldBaselineSubmissionTests
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
                await Freeze(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "generation", "login", "game", "world", "snapshot" })
                    foreach (string phase in new[] { "capture", "prepare", "metadata", "upload", "publication" })
                    { await Ownership(change, phase); count++; }
                foreach (string phase in new[] { "metadata", "upload" })
                    foreach (string failure in new[] { "network", "null", "missing-result", "rejected", "exception", "canceled" })
                    { await Failure(phase, failure); count++; }
                foreach (string id in new[] { "accepted", "absent-config", "null-id", "", "  " }) { await AcceptedId(id); count++; }
                foreach (string missing in new[] { "server", "user", "colony", "id", "payload", "geometry", "layers", "centers" }) { Invalid(missing); count++; }
                foreach (string phase in new[] { "metadata", "upload" })
                {
                    await Admission(phase); count++;
                    foreach (bool oldFails in new[] { false, true })
                        foreach (bool oldFirst in new[] { false, true }) { await Replacement(phase, oldFails, oldFirst); count++; }
                }
                await Publication(); count++;
                Discard(); Runtime(); count += 2;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: world baseline submission ({count} scenarios)");
    }
    private static async Task Freeze()
    {
        var f = new Fixture(); var config = Configuration(); byte[] payload = { 1, 2, 3 };
        string before = ClientProtocolJson.Serialize(config);
        var capture = f.Module.Capture();
        f.Ports.Snapshot = "latest"; capture = f.Module.Capture();
        var prepared = f.Module.Prepare(capture, config, payload)!;
        Check(ClientProtocolJson.Serialize(config) == before, "Preparation mutated caller data.");
        var geometry = config.TileGeometry; config.TileGeometry = null;
        string expected = ClientProtocolJson.Serialize(config);
        config.TileGeometry = geometry;
        // Mutating the caller's graph after main-thread preparation must not reach either request.
        config.SeedString = "changed"; config.Features[0].Label = "changed";
        config.Extensions[0].Metadata["culture"] = "changed"; config.FeatureNameCatalogs[0].Features[0].Label = "changed";
        config.PlayerColonySites[0].ColonyId = "changed"; config.TileGeometry!.Layers.Clear(); payload[0] = 99;
        f.Before = phase =>
        {
            if (phase == "upload") f.MetadataReply.WorldConfiguration!.WorldConfigurationId = "mutated-response";
            return Task.CompletedTask;
        };
        var result = await f.Module.SubmitAsync(prepared);
        Check(result?.Response?.Result?.Accepted == true && f.Trace.SequenceEqual(new[] { "metadata", "upload" }), "Submission order changed.");
        Check(ClientProtocolJson.Serialize(f.Sent!) == expected && f.Bytes!.SequenceEqual(new byte[] { 1, 2, 3 }), "Captured graph or bytes leaked changes.");
        Check(f.Contexts.All(c => c.UserId == "alice" && c.ColonyId == "colony" && c.CurrentSnapshotId == "latest")
            && f.UploadId == "accepted" && result!.Response!.WorldConfiguration!.WorldConfigurationId == "accepted", "Context/accepted receipt was reread.");
        Check(f.Module.Complete(prepared, () => { }), "Successful submission could not publish.");
    }
    private static async Task Ownership(string change, string phase)
    {
        var f = new Fixture(); var capture = f.Module.Capture();
        if (phase == "capture") f.Change(change);
        var prepared = f.Module.Prepare(capture, Configuration(), new byte[] { 1 });
        bool allowed = change == "snapshot";
        if (phase == "capture" && !allowed)
        { Check(prepared is null && f.Trace.Count == 0, "Old capture accepted: " + change); return; }
        Check(prepared is not null, "Current capture was rejected.");
        if (phase == "prepare") f.Change(change);
        if (phase is "metadata" or "upload") f.Before = at => { if (at == phase) f.Change(change); return Task.CompletedTask; };
        var result = await f.Module.SubmitAsync(prepared!);
        if (phase == "publication") f.Change(change);
        int published = 0; f.Module.Complete(prepared!, () => published++);
        int expectedCalls = allowed || phase == "publication" ? 2 : phase == "prepare" ? 0 : phase == "metadata" ? 1 : 2;
        Check(f.Trace.Count == expectedCalls && published == (allowed ? 1 : 0), "Old owner crossed boundary: " + change + "/" + phase);
        Check((result is not null) == (allowed || phase == "publication"), "Obsolete request returned publishable result.");
        Check(f.Contexts.All(c => c.UserId == "alice" && c.ColonyId == "colony" && c.ServerBaseUrl == "http://localhost" && c.AuthToken == "auth"), "Request adopted successor identity.");
    }
    private static async Task Failure(string phase, string failure)
    {
        var f = new Fixture { FaultPhase = phase, Fault = failure }; var prepared = f.Prepare();
        bool threw = false;
        try
        {
            var result = await f.Module.SubmitAsync(prepared);
            Check(result is not null && (!result.Success || result.Response?.Result?.Accepted != true), "Failure reported success: " + phase + "/" + failure);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException { Message: "network failure" }) { threw = true; }
        Check(threw == (failure is "exception" or "canceled") && f.Trace.Count == (phase == "metadata" ? 1 : 2), "Failure boundary changed.");
        Check(!f.Module.Complete(prepared, () => throw new InvalidOperationException("Published failure")), "Failed operation could publish.");
        f.Fault = null; var retry = f.Prepare();
        Check((await f.Module.SubmitAsync(retry))?.Response?.Result?.Accepted == true, "Failure retained admission.");
    }
    private static async Task AcceptedId(string id)
    {
        var f = new Fixture();
        if (id == "absent-config") f.MetadataReply.WorldConfiguration = null;
        else f.MetadataReply.WorldConfiguration!.WorldConfigurationId = id == "null-id" ? null! : id;
        var prepared = f.Prepare(); var result = await f.Module.SubmitAsync(prepared);
        bool valid = id.Length > 0 && id != "  ";
        Check((result?.Response?.Result?.Accepted == true) == valid && f.Trace.Count == (valid ? 2 : 1), "Missing accepted ID skipped substrate but reported success.");
        if (valid) Check(f.UploadId == (id is "absent-config" or "null-id" ? "source" : id), "Wrong substrate identity.");
    }
    private static void Invalid(string missing)
    {
        var f = new Fixture(); var config = Configuration(); byte[] payload = { 1 };
        switch (missing)
        {
            case "server": f.Ports.Server = ""; break;
            case "user": f.Ports.User = ""; break;
            case "colony": f.Ports.Colony = ""; break;
            case "id": config.WorldConfigurationId = " "; break;
            case "payload": payload = Array.Empty<byte>(); break;
            case "geometry": config.TileGeometry = null; break;
            case "layers": config.TileGeometry!.Layers.Clear(); break;
            case "centers": config.TileGeometry!.Layers[0].TileCenters.Clear(); break;
        }
        bool threw = false;
        try { f.Module.Prepare(f.Module.Capture(), config, payload); } catch (InvalidOperationException) { threw = true; }
        Check(threw && f.Trace.Count == 0, "Incomplete input reached submission: " + missing);
    }
    private static async Task Admission(string phase)
    {
        var f = new Fixture(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Before = at => at == phase ? gate.Task : Task.CompletedTask;
        var prepared = f.Prepare();
        Check(!f.Module.Complete(prepared, () => { }), "Unsubmitted preparation published.");
        var work = f.Module.SubmitAsync(prepared);
        Check(!work.IsCompleted && !f.Module.Complete(prepared, () => { }), "Intermediate metadata receipt published success.");
        Check(await f.Module.SubmitAsync(prepared) is null, "Duplicate invocation sent another request.");
        Check(f.Module.Prepare(f.Module.Capture(), Configuration(), new byte[] { 1 }) is null, "Concurrent preparation replaced active write.");
        gate.SetResult(); Check((await work)?.Response?.Result?.Accepted == true, "Duplicate admission canceled original write.");
        Check(f.Trace.Count == 2 && await f.Module.SubmitAsync(prepared) is null, "Consumed submission ran twice.");
    }
    private static async Task Replacement(string phase, bool oldFails, bool oldFirst)
    {
        var f = new Fixture(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Before = async at => { if (at == phase) { await gate.Task; if (oldFails) throw new InvalidOperationException("late old failure"); } };
        var old = f.Prepare(); var oldWork = f.Module.SubmitAsync(old);
        f.Change("world");
        var nextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Before = at => at == "metadata" ? nextGate.Task : Task.CompletedTask;
        var next = f.Prepare(); var nextWork = f.Module.SubmitAsync(next);
        if (oldFirst)
        {
            gate.SetResult(); Check(await oldWork is null, "Obsolete response/exception escaped.");
            Check(f.Module.Prepare(f.Module.Capture(), Configuration(), new byte[] { 1 }) is null, "Old finally released new request.");
            nextGate.SetResult();
        }
        else { nextGate.SetResult(); await nextWork; gate.SetResult(); Check(await oldWork is null, "Late obsolete work escaped."); }
        Check((await nextWork)?.Response?.Result?.Accepted == true && f.Module.Complete(next, () => { })
            && !f.Module.Complete(old, () => { }), "Old request poisoned successor publication.");
    }
    private static async Task Publication()
    {
        var f = new Fixture(); var old = f.Prepare(); await f.Module.SubmitAsync(old);
        var next = f.Prepare();
        Check(!f.Module.Complete(old, () => { }), "Older queued receipt overwrote a new same-world write.");
        await f.Module.SubmitAsync(next); int count = 0;
        Check(f.Module.Complete(next, () => { count++; Check(!f.Module.Complete(next, () => count++), "Reentrant publication succeeded."); })
            && !f.Module.Complete(next, () => count++) && count == 1, "Success publication ran twice.");
    }
    private static void Discard()
    {
        var f = new Fixture(); var old = f.Prepare(); f.Module.Discard(old);
        var next = f.Prepare(); f.Module.Discard(old);
        Check(f.Module.IsCurrent(next) && !f.Module.IsCurrent(old), "Discarding old preparation cleared replacement.");
        f.Module.Discard(next);
    }
    private static void Runtime()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        try
        {
            Current.CreatingWorld = null; Current.Game = null;
            var runtime = new ClientWorldBaselineRuntime(); Check(!runtime.Bind().IsCurrent, "Missing game was eligible for a baseline write.");
            Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            Current.Game.World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            var owner = Current.Game; var world = owner.World; var bound = runtime.Bind();
            Check(bound.IsCurrent, "Runtime failed to bind world.");
            owner.World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World)); Check(!bound.IsCurrent, "World replacement not detected.");
            owner.World = world; Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game)); Current.Game.World = world;
            Check(!bound.IsCurrent, "Game replacement with same world was accepted.");
        }
        finally { Current.Game = game; Current.CreatingWorld = creating; }
    }
    private static ModWorldConfigurationDto Configuration() => new()
    {
        WorldConfigurationId = "source", ConfiguredByUserId = "alice", ConfiguredByColonyId = "colony", ConfiguredAtUtc = "2026-10-18T00:00:00Z",
        SeedString = "测试 İ", PlanetCoverage = "0.3", OverallRainfall = "Normal", OverallTemperature = "Cold", OverallPopulation = "High", LandmarkDensity = "Low", TileCount = "2",
        StorytellerDefName = "Cassandra", DifficultyDefName = "Custom", DifficultyValuesXml = "<difficulty><threatScale>1.37</threatScale></difficulty>", GameLanguage = "French",
        FactionDefNames = new() { "Tribe", "Tribe" }, Features = new() { new() { DefName = "Island", Label = "île", DrawCenterX = 1.25f } },
        FeatureNameCatalogs = new() { new() { Language = "French", WorldConfigurationId = "source", Features = new() { new() { Label = "île" } } } },
        Factions = new() { new() { DefName = "Tribe", Name = "测试" } }, Roads = new() { new() { FromTile = 0, ToTile = 1, RoadDefName = "Road" } },
        WorldObjects = new() { new() { Tile = 1, DefName = "Settlement", Label = "家" } }, PlayerColonySites = new() { new() { ColonyId = "colony", UserId = "alice", Tile = 0 } },
        Extensions = new() { new() { ProviderId = "pollution", PayloadJson = "{\"value\":0.25}", Metadata = new() { ["culture"] = "tr-TR" } } },
        TileGeometry = new() { Layers = new() { new() { LayerId = 7, TileCenters = new() { new() { Tile = 0, X = 1.5f } } } } }
    };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldBaselineRuntime, IClientWorldBaselineTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldBaselineSubmission Module;
        internal object World = new();
        internal readonly List<string> Trace = new();
        internal readonly List<ClashOfRimClientNetworkContext> Contexts = new();
        internal ModWorldConfigurationDto? Sent;
        internal byte[]? Bytes;
        internal string? UploadId, FaultPhase, Fault;
        internal Func<string, Task>? Before;
        internal ModSubmitWorldConfigurationResponseDto MetadataReply = new() { Result = new() { Accepted = true }, WorldConfiguration = new() { WorldConfigurationId = "accepted" } };
        internal Fixture()
        { ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Module = new(Session, this, this); }
        internal ClientWorldBaselineSubmission.Prepared Prepare() => Module.Prepare(Module.Capture(), Configuration(), new byte[] { 1, 2 }) ?? throw new InvalidOperationException("Preparation unexpectedly busy.");
        public IClientWorldBaselineSession Bind() => new Bound(this, Ports.GameOwner, World);
        private sealed record Bound(Fixture Owner, object Game, object World) : IClientWorldBaselineSession
        {
            public bool IsCurrent => ReferenceEquals(Owner.Ports.GameOwner, Game) && ReferenceEquals(Owner.World, World);
            public string? Language => "French";
            public ModWorldConfigurationDto ReadWorld(string user, string colony, ModColonyAppearanceDto? appearance) => throw new NotSupportedException();
            public IReadOnlyList<ModWorldConfigurationExtensionDto> ReadExtensions(string user, string colony, string world) => throw new NotSupportedException();
            public byte[]? CaptureSubstrate(ModWorldTileGeometryDto? geometry, out string? failure) => throw new NotSupportedException();
        }
        private async Task Enter(string phase, ClashOfRimClientNetworkContext context)
        { Trace.Add(phase); Contexts.Add(context); if (Before is not null) await Before(phase); }
        private ClashOfRimClientNetworkResult<T> Reply<T>(string phase, T response)
        {
            if (FaultPhase == phase)
                switch (Fault)
                {
                    case "network": return ClashOfRimClientNetworkResult<T>.Failed("offline", "无法连接");
                    case "null": return ClashOfRimClientNetworkResult<T>.Ok(default!);
                    case "exception": throw new InvalidOperationException("network failure");
                    case "canceled": throw new OperationCanceledException();
                }
            return ClashOfRimClientNetworkResult<T>.Ok(response);
        }
        private ModProtocolResponseDto? Result(string phase) => FaultPhase == phase && Fault == "missing-result" ? null : new() { Accepted = FaultPhase != phase || Fault != "rejected", Message = "服务器结果" };
        public async Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
        { Sent = configuration; await Enter("metadata", context); MetadataReply.Result = Result("metadata"); return Reply("metadata", MetadataReply); }
        public async Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token)
        { UploadId = worldId; Bytes = payload; await Enter("upload", context); return Reply("upload", new ModUploadWorldSubstrateResponseDto { Result = Result("upload") }); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "server": Ports.Server += "/other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "auth": Ports.Auth = "other"; break;
                case "game": Ports.GameOwner = new(); break;
                case "world": World = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "same-credentials" }); break;
            }
        }
    }
}
