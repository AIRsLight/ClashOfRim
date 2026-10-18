using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldBaselineCaptureTests
{
    internal static void Run()
    {
        int count = 0; var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Inputs(); count++;
                foreach (string change in new[] { "user", "server", "colony", "auth", "game", "world", "generation", "login", "snapshot", "language", "same-language" })
                    foreach (string stage in new[] { "world", "storyteller", "extensions", "substrate" }) { Ownership(change, stage); count++; }
                foreach (string field in new[] { "seed", "coverage", "factions", "features", "storyteller", "difficulty", "xml-failure", "substrate" })
                    foreach (bool runtime in new[] { false, true }) { Validation(field, runtime); count++; }
                foreach (string coverage in new[] { "0", "-1", "NaN", "Infinity", "0,3", "0.3", "30", "1e-1" }) { Coverage(coverage); count++; }
                foreach (string stage in new[] { "world", "storyteller", "extensions", "substrate" })
                { Retry(stage); Reentry(stage); count += 2; }
                Sites(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: world baseline capture ({count} scenarios)");
    }
    private static void Inputs()
    {
        var f = new Fixture(); var appearance = new ModColonyAppearanceDto { ColorHex = "#123456", IconDefName = "icon" };
        f.Appearance = appearance;
        var result = f.Capture(); var config = result.Configuration!;
        Check(result.Error is null && result.Prepared is not null && f.Trace.SequenceEqual(new[] { "world", "storyteller", "extensions", "substrate" }), "Capture order/input missing.");
        Check(config.ConfiguredByUserId == "alice" && config.ConfiguredByColonyId == "colony" && config.WorldConfigurationId.StartsWith("world:", StringComparison.Ordinal)
            && DateTimeOffset.TryParse(config.ConfiguredAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _), "Configuration identity/time not owned by capture.");
        Check(config.SeedString == "seed" && config.PlanetCoverage == "0.3" && config.DifficultyValuesXml!.Contains("1.37")
            && config.GameLanguage == "French" && config.Extensions[0].Metadata["value"] == "0.25", "World/storyteller/extensions lost values.");
        Check(f.ExtensionIdentity == ("alice", "colony", config.WorldConfigurationId) && f.WorldIdentity == ("alice", "colony"), "Capture read wrong identity.");
        Check(!ReferenceEquals(f.SeenAppearance, appearance) && appearance.ColorHex == "#123456", "Runtime borrowed mutable appearance.");
        config.Extensions[0].Metadata["value"] = "changed"; config.SeedString = "changed";
        f.Bytes[0] = 99; appearance.ColorHex = "changed";
        var sent = f.Submission.SubmitAsync(result.Prepared!).GetAwaiter().GetResult();
        Check(sent?.Response?.Result?.Accepted == true && f.Sent!.SeedString == "seed" && f.Sent.Extensions[0].Metadata["value"] == "0.25"
            && f.Sent.PlayerColonySites[0].Appearance!.ColorHex == "#123456" && f.SentBytes![0] == 1, "Prepared capture was not frozen.");
    }
    private static void Ownership(string change, string stage)
    {
        var f = new Fixture(); f.OnStage = at => { if (at == stage) f.Change(change); };
        var result = f.Capture(); bool allowed = change is "snapshot" or "same-language";
        Check((result.Prepared is not null) == allowed && result.Error is null, "Stale world input accepted: " + change + "/" + stage);
        int expected = Array.IndexOf(new[] { "world", "storyteller", "extensions", "substrate" }, stage) + 1;
        Check(f.Trace.Count == (allowed ? 4 : expected), "Capture continued after ownership changed.");
        if (allowed) f.Submission.Discard(result.Prepared!);
    }
    private static void Validation(string field, bool runtime)
    {
        var f = new Fixture();
        switch (field)
        {
            case "seed": f.Config.SeedString = " "; break;
            case "coverage": f.Config.PlanetCoverage = "invalid"; break;
            case "factions": f.Config.FactionDefNames.Clear(); break;
            case "features": f.Config.Features.Clear(); break;
            case "storyteller": f.Story = f.Story with { Storyteller = null }; break;
            case "difficulty": f.Story = f.Story with { Difficulty = null }; break;
            case "xml-failure": f.Story = f.Story with { ValuesXml = null, CaptureFailure = "Scribe busy" }; break;
            case "substrate": f.SubstrateFailure = "Scribe busy"; break;
        }
        bool expected = field is "xml-failure" or "substrate" || (runtime ? field is "storyteller" or "difficulty" : field is "seed" or "coverage" or "factions" or "features");
        var result = f.Capture(runtime);
        Check((result.Error is not null) == expected && (result.Prepared is null) == expected, "Capture validation changed entry semantics: " + field + "/" + runtime);
        if (expected && field != "substrate") Check(!f.Trace.Contains("substrate"), "Invalid input invoked Scribe substrate capture.");
        if (field == "xml-failure") Check(result.Error?.Code == "StorytellerValues" && !f.Trace.Contains("extensions"), "Failed custom difficulty capture became a preset upload.");
        if (result.Prepared is not null) f.Submission.Discard(result.Prepared);
    }
    private static void Coverage(string value)
    {
        var f = new Fixture(); f.Config.PlanetCoverage = value; var result = f.Capture();
        bool valid = value is "0.3" or "30" or "1e-1";
        Check((result.Prepared is not null) == valid && (!valid || result.Configuration!.PlanetCoverage == value), "Coverage was reinterpreted by current culture.");
        if (result.Prepared is not null) f.Submission.Discard(result.Prepared);
    }
    private static void Retry(string stage)
    {
        var f = new Fixture(); f.OnStage = at => { if (at == stage) throw new InvalidOperationException("fixture fault"); };
        bool caught = false;
        try { f.Capture(); } catch (InvalidOperationException ex) when (ex.Message == "fixture fault") { caught = true; }
        f.OnStage = null;
        Check(caught && f.Capture().Prepared is not null, "Capture failure leaked admission/reentry guard: " + stage);
    }
    private static void Reentry(string stage)
    {
        var f = new Fixture(); bool nested = false;
        f.OnStage = at => { if (at == stage) { nested = true; Check(f.Capture().Prepared is null, "Nested capture ran during Scribe/provider work."); } };
        Check(f.Capture().Prepared is not null && nested && f.Trace.Count == 4, "Reentry invalidated original capture.");
    }
    private static void Sites()
    {
        ModPlayerColonySiteDto Site(string id, string? map, int layer, int tile) => new() { WorldObjectId = id, MapUniqueId = map, TileLayerId = layer, Tile = tile };
        var maps = new[] { Site("home", "Map_1", 7, 3), Site("bad-layer", "Map_2", 9, 0), Site("bad-tile", "Map_3", 0, 3), Site("negative", "Map_4", 7, -1), Site("a", "tile:1,0", 0, 0) };
        var objects = new[] { Site("home", "Map_other", 0, 1), Site("other", null, 7, 3), Site("surface", null, 0, 1), Site("orbital", null, 7, 1), Site("too-large", null, 7, 4) };
        var sites = ClientWorldColonySites.Merge(maps, objects, new Dictionary<int, int> { [0] = 2, [7] = 4 });
        Check(sites.Select(s => s.WorldObjectId).SequenceEqual(new[] { "a", "surface", "orbital", "home" }), "Layer bounds, map priority, structural keys or order changed.");
        Check(ReferenceEquals(sites.Last(), maps[0]) && maps.Length == 5 && objects.Length == 5, "Site merger changed caller collections/precedence.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientWorldBaselineRuntime, IClientWorldBaselineSession, IClientWorldBaselineTransport, IClientStorytellerRuntime, IClientStorytellerSession
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldBaselineSubmission Submission;
        internal readonly ClientWorldBaselineCapture Module;
        internal readonly List<string> Trace = new();
        internal object World = new();
        private object boundGame = null!, boundWorld = null!;
        internal string CurrentLanguage = "French";
        internal ModColonyAppearanceDto? Appearance, SeenAppearance;
        internal Action<string>? OnStage;
        internal string? SubstrateFailure;
        internal byte[] Bytes = { 1, 2, 3 };
        internal byte[]? SentBytes;
        internal ModWorldConfigurationDto? Sent;
        internal (string, string) WorldIdentity;
        internal (string, string, string) ExtensionIdentity;
        internal ClientStorytellerBaseline Story = new("story", "difficulty", "<saveable><threatScale>1.37</threatScale></saveable>");
        internal ModWorldConfigurationDto Config = new()
        {
            SeedString = "seed", PlanetCoverage = "0.3", GameLanguage = "French", FactionDefNames = new() { "tribe" },
            Features = new() { new() { DefName = "island" } },
            TileGeometry = new() { Layers = new() { new() { TileCenters = new() { new() { Tile = 0 } } } } }
        };
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            Submission = new(Session, this, this);
            Module = new(Submission, new ClientStorytellerConfiguration(Session, new ClientWorldSetupState(Session), this));
        }
        internal ClientWorldBaselineCapture.Result Capture(bool runtime = false) => Module.Capture(Submission.Capture(), Appearance, runtime);
        public IClientWorldBaselineSession Bind() { boundGame = Ports.GameOwner; boundWorld = World; return this; }
        IClientStorytellerSession IClientStorytellerRuntime.Bind() => this;
        public bool IsCurrent => ReferenceEquals(boundGame, Ports.GameOwner) && ReferenceEquals(boundWorld, World);
        public bool IsPlaying => true;
        public string? Language => CurrentLanguage;
        private void Stage(string name) { Trace.Add(name); OnStage?.Invoke(name); }
        public ModWorldConfigurationDto ReadWorld(string user, string colony, ModColonyAppearanceDto? appearance)
        {
            var result = ClientProtocolJson.Copy(Config); SeenAppearance = appearance; WorldIdentity = (user, colony);
            result.PlayerColonySites.Add(new() { UserId = user, ColonyId = colony, Appearance = appearance });
            Stage("world"); return result;
        }
        public IReadOnlyList<ModWorldConfigurationExtensionDto> ReadExtensions(string user, string colony, string world)
        { ExtensionIdentity = (user, colony, world); Stage("extensions"); return new[] { new ModWorldConfigurationExtensionDto { ProviderId = "test", Metadata = new() { ["value"] = "0.25" } } }; }
        public byte[]? CaptureSubstrate(ModWorldTileGeometryDto? geometry, out string? failure)
        { Stage("substrate"); failure = SubstrateFailure; return failure is null ? Bytes : null; }
        ClientStorytellerBaseline IClientStorytellerSession.Capture() { Stage("storyteller"); return Story; }
        public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline) => throw new NotSupportedException();
        public void LockCommitment() => throw new NotSupportedException();
        public Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
        { Sent = configuration; return Task.FromResult(ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>.Ok(new() { Result = new() { Accepted = true } })); }
        public Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string world, byte[] payload, CancellationToken token)
        { SentBytes = payload; return Task.FromResult(ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>.Ok(new() { Result = new() { Accepted = true } })); }
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
                case "language": CurrentLanguage = "German"; break;
                case "same-language": CurrentLanguage = " french "; break;
            }
        }
    }
}
