using AIRsLight.ClashOfRim.Compatibility;
using System.Globalization;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Sessions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientWorldProtocolTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Preparation(true, true, ""); await Preparation(false, false, "complete mod list");
                await Capture(false); await Capture(true); count += 4;
                foreach (string operation in new[] { "hello", "prepare", "read", "submit", "names", "upload", "download" })
                { await Wire(operation); count++; }
                foreach (bool accepted in new[] { false, true })
                foreach (bool details in new[] { false, true })
                foreach (string message in new[] { "complete mod list", "完整模组列表", "liste complète", "" })
                { await Negotiation(accepted, details, message); count++; }
                foreach (string fault in new[] { "empty", "exception", "cancel", "reset" }) { await NegotiationFault(fault); count++; }
                foreach (int flags in Enumerable.Range(0, 8)) { await ReadOptions(flags); count++; }
                foreach (string operation in new[] { "submit", "upload" })
                foreach (string fault in new[] { "success", "login-rejected", "rejected", "http", "cancel", "reset" })
                { await Setup(operation, fault); count++; }
                await Admission(); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: world protocol ({count} scenarios)");
    }
    private static ClashOfRimClientNetworkContext Context(string? auth = null) => new("http://localhost", "alice", "colony", "base", null, "pw", auth);
    private static async Task Preparation(bool accepted, bool requires, string message)
    {
        using var hooks = new Hooks(); int calls = 0;
        using var http = new HttpClient(new Handler((request, token) =>
        {
            calls++;
            return Task.FromResult(Json(new ModPrepareWorldSessionResponseDto { Result = new() { Accepted = accepted, Message = message }, RequiresFullCompatibilityManifest = requires }));
        }));
        await new ClashOfRimModNetworkClient(http, Context()).PrepareWorldSessionAsync();
        Check(calls == 1, accepted ? "Accepted world preparation retried a contradictory detail flag." : "World preparation inferred retry from localized prose.");
    }
    private static async Task Capture(bool binary)
    {
        using var hooks = new Hooks();
        var configuration = new ModWorldConfigurationDto { DifficultyValuesXml = "original", PlanetCoverage = "0.3", Features = new() { new() { Label = "original" } } };
        byte[] payload = Encoding.UTF8.GetBytes("original"); string? captured = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route)
            {
                configuration.DifficultyValuesXml = "changed"; configuration.Features[0].Label = "changed"; payload[0] = (byte)'X';
                return Json(new ModLoginResponseDto { Result = new() { Accepted = true }, AuthToken = "temporary", SessionId = "setup" });
            }
            if (path == ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route) return Json(new ModLogoutResponseDto { Result = new() { Accepted = true } });
            if (binary)
            {
                var parts = (MultipartFormDataContent)request.Content!;
                captured = await parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "payload").ReadAsStringAsync(token);
                return Json(new ModUploadWorldSubstrateResponseDto { Result = new() { Accepted = true } });
            }
            captured = await request.Content!.ReadAsStringAsync(token);
            return Json(new ModSubmitWorldConfigurationResponseDto { Result = new() { Accepted = true } });
        }));
        var client = new ClashOfRimModNetworkClient(http, Context());
        if (binary) await client.UploadWorldSubstrateAsync("world", payload); else await client.SubmitWorldConfigurationAsync(configuration);
        Check(binary ? captured == "original" : captured is not null && captured.Contains("original") && !captured.Contains("changed"), "World setup borrowed mutable " + (binary ? "substrate bytes" : "configuration") + " while awaiting login.");
    }
    private static ModWorldConfigurationDto Configuration() => new()
    {
        WorldConfigurationId = "世界", SeedString = "seed", PlanetCoverage = "0.3", OverallRainfall = "Normal", OverallTemperature = "Cold",
        OverallPopulation = "High", LandmarkDensity = "Normal", TileCount = "12345", StorytellerDefName = "Cassandra", DifficultyDefName = "custom",
        DifficultyValuesXml = "<difficulty><threatScale>1.37</threatScale></difficulty>", GameLanguage = "zh-CN",
        Features = new() { new() { DefName = "山", Label = "İ العربية", DrawCenterX = 1.25f } },
        TileGeometry = new() { Layers = new() { new() { LayerId = 3, LayerDefName = "Orbit", TileCenters = new() { new() { Tile = 5, X = 1.25f, Y = -2.5f, Z = 3.75f } } } } },
        Extensions = new() { new() { ProviderId = "Ideology", Kind = "culture", SchemaVersion = "1", PayloadJson = "{\"ratio\":0.3}", Metadata = new() { ["language"] = "İ 中文" } } }
    };
    private static async Task Wire(string operation)
    {
        using var hooks = new Hooks(); var env = new Environment(); var requests = new List<(string Method, string Route, string Body, string? Auth, string? Payload)>();
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户", "colony", "snapshot", "ticket", "password", "borrowed");
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string body = ""; string? payload = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                body = await multipart.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "request").ReadAsStringAsync(token);
                payload = Convert.ToBase64String(await multipart.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "payload").ReadAsByteArrayAsync(token));
            }
            else if (request.Content is not null) body = await request.Content.ReadAsStringAsync(token);
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body,
                request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var values) ? values.Single() : null, payload));
            return operation == "download" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0, 1, 255 }) }
                : Json(new ModPrepareWorldSessionResponseDto { Result = new() { Accepted = true } });
        }));
        var world = Create(http, env); var facade = new ClashOfRimModNetworkClient(http, context); var configuration = Configuration(); byte[] bytes = { 0, 1, 255 };
        bool first = operation switch
        {
            "hello" => (await world.HelloAsync(context, default)).Success,
            "prepare" => (await world.PrepareAsync(context, true, default)).Success,
            "read" => (await world.ReadAsync(context, false, true, false, default)).Success,
            "submit" => (await world.SubmitAsync(context, configuration, default)).Success,
            "names" => (await world.SubmitNamesAsync(context, "中文", "世界", configuration.Features, default)).Success,
            "upload" => (await world.UploadSubstrateAsync(context, "世界", bytes, default)).Success,
            _ => (await world.DownloadSubstrateAsync(context, "世界", default)).Response!.SequenceEqual(bytes)
        };
        bool second = operation switch
        {
            "hello" => (await facade.ServerHelloAsync()).Success,
            "prepare" => (await facade.PrepareWorldSessionAsync(true)).Success,
            "read" => (await facade.GetWorldConfigurationAsync(false, true, false)).Success,
            "submit" => (await facade.SubmitWorldConfigurationAsync(configuration)).Success,
            "names" => (await facade.SubmitWorldFeatureNamesAsync("中文", "世界", configuration.Features)).Success,
            "upload" => (await facade.UploadWorldSubstrateAsync("世界", bytes)).Success,
            _ => (await facade.DownloadWorldSubstrateAsync("世界")).Response!.SequenceEqual(bytes)
        };
        Check(first && second && requests.Count == 2 && requests[0] == requests[1], "Module/facade wire contracts differ: " + operation);
        var sent = requests[0];
        var kind = operation switch { "hello" => ProtocolMessageKind.ServerHello, "prepare" => ProtocolMessageKind.PrepareWorldSession,
            "read" => ProtocolMessageKind.GetWorldConfiguration, "submit" => ProtocolMessageKind.SubmitWorldConfiguration,
            "names" => ProtocolMessageKind.SubmitWorldFeatureNames, "upload" => ProtocolMessageKind.UploadWorldSubstrate, _ => ProtocolMessageKind.DownloadWorldSubstrate };
        Check(sent.Route == "/base/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/') && sent.Method == (operation == "hello" ? "GET" : "POST"), "World route/context was not captured.");
        if (operation == "hello") { Check(sent.Body == "", "Hello acquired a request body."); return; }
        using var json = JsonDocument.Parse(sent.Body); var root = json.RootElement;
        Check(root.GetProperty("userId").GetString() == context.UserId && root.GetProperty("protocolVersion").GetString() == ClashOfRimVersion.ProtocolVersion, "World request lost identity/protocol.");
        int fields = operation switch { "submit" => 5, "upload" or "download" => 6, _ => 8 };
        Check(root.EnumerateObject().Count() == fields, "World request field set changed: " + operation);
        if (operation != "prepare") Check(root.GetProperty("colonyId").GetString() == "colony", "World request lost colony.");
        if (operation == "submit")
        {
            Check(root.GetProperty("authToken").GetString() == "borrowed", "World submit lost borrowed authentication.");
            var captured = ClientProtocolJson.Deserialize<ModSubmitWorldConfigurationRequestDto>(sent.Body, static type => type).Configuration!;
            Check(ClientProtocolJson.Serialize(captured) == ClientProtocolJson.Serialize(configuration) && captured.TileGeometry!.Layers[0].TileCenters[0].Y == -2.5f
                && captured.Extensions[0].Metadata["language"] == "İ 中文" && captured.PlanetCoverage == "0.3", "World parameters, layer geometry or culture data changed.");
        }
        else Check(root.GetProperty("steamAuthTicket").GetString() == "ticket" && root.GetProperty("password").GetString() == "password", "World request credentials changed.");
        if (operation == "prepare") Check(root.GetProperty("createAccountIfMissing").GetBoolean(), "Account creation intent was lost.");
        if (operation is "upload" or "download") Check(sent.Auth == "borrowed", "Binary world protocol lost auth header.");
        if (operation == "upload") Check(sent.Payload == Convert.ToBase64String(bytes), "World substrate bytes changed.");
        if (operation == "names") Check(root.GetProperty("language").GetString() == "中文" && root.GetProperty("features")[0].GetProperty("label").GetString() == "İ العربية", "Feature language/labels changed.");
    }
    private static async Task Negotiation(bool accepted, bool details, string message)
    {
        ClientSessionWork.Reset(); var env = new Environment(); var bodies = new List<ModPrepareWorldSessionRequestDto>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            bodies.Add(ClientProtocolJson.Deserialize<ModPrepareWorldSessionRequestDto>(await request.Content!.ReadAsStringAsync(token), static type => type));
            env.CurrentId = "new-id"; env.CurrentSummary = "new-summary";
            return Json(new ModPrepareWorldSessionResponseDto { Result = new() { Accepted = accepted, Message = message },
                RequiresFullCompatibilityManifest = details, RequestedCompatibilityPackageIds = new() { "core", "dlc" } });
        }));
        var result = await Create(http, env).PrepareAsync(Context(), true, default); bool retry = !accepted && details;
        Check(result.Success && bodies.Count == (retry ? 2 : 1) && env.Builds == (retry ? 1 : 0) && env.Entries == 1, "Negotiation retried prose/acceptance or exceeded one detail request.");
        Check(bodies.All(body => body.CompatibilityManifestId == "id" && body.CompatibilityManifestSummaryJson == "summary" && body.CreateAccountIfMissing
            && body.UserId == "alice" && body.SteamAuthTicket == "alice" && body.Password == "pw"), "Retry reread identity/summary or lost setup intent.");
        if (retry) Check(bodies[1].CompatibilityManifestJson == "{}" && env.Requested!.SequenceEqual(new[] { "core", "dlc" }), "Requested detail subset was lost.");
    }
    private static async Task NegotiationFault(string fault)
    {
        ClientSessionWork.Reset(); var env = new Environment { Full = fault == "empty" ? " " : "{}", ThrowBuild = fault == "exception" };
        using var cancellation = new CancellationTokenSource(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++; if (fault == "cancel") cancellation.Cancel(); if (fault == "reset") ClientSessionWork.Reset();
            return Task.FromResult(Json(new ModPrepareWorldSessionResponseDto { Result = new() { Accepted = false }, RequiresFullCompatibilityManifest = true }));
        }));
        try
        {
            var result = await Create(http, env).PrepareAsync(Context(), false, cancellation.Token);
            Check(fault != "exception" && !result.Success && calls == 1 && env.Builds == (fault == "empty" ? 1 : 0), "Failed/canceled negotiation retried.");
            if (fault == "empty") Check(result.ErrorCode == "CompatibilityManifestBuildFailed", "Empty manifest failure changed.");
        }
        catch (InvalidOperationException ex) when (fault == "exception" && ex.Message == "manifest") { Check(calls == 1, "Manifest exception sent another request."); }
    }
    private static async Task ReadOptions(int flags)
    {
        ClientSessionWork.Reset(); var env = new Environment();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var dto = ClientProtocolJson.Deserialize<ModGetWorldConfigurationRequestDto>(await request.Content!.ReadAsStringAsync(token), static type => type);
            Check(dto.IncludeGenerationBaseline == ((flags & 1) != 0) && dto.IncludePlayerColonySites == ((flags & 2) != 0)
                && dto.IncludeWorldExtensions == ((flags & 4) != 0), "World configuration read options were collapsed.");
            return Json(new ModGetWorldConfigurationResponseDto { Result = new() { Accepted = true } });
        }));
        Check((await Create(http, env).ReadAsync(Context(), (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, default)).Success, "Configuration read failed.");
    }
    private static async Task Setup(string operation, string fault)
    {
        ClientSessionWork.Reset(); var env = new Environment(); var calls = new List<string>(); using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route)
            {
                calls.Add("login"); return Json(new ModLoginResponseDto { Result = new() { Accepted = fault != "login-rejected" }, AuthenticatedUserId = "canonical", AuthToken = "owned", SessionId = "owned-session" });
            }
            if (path == ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route)
            {
                calls.Add("logout"); using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Check(json.RootElement.GetProperty("sessionId").GetString() == "owned-session" && json.RootElement.GetProperty("userId").GetString() == "canonical", "Setup released another identity/session.");
                Check(!token.IsCancellationRequested, "Setup cleanup borrowed the canceled operation token.");
                return Json(new ModLogoutResponseDto { Result = new() { Accepted = true } });
            }
            calls.Add(operation);
            string body = request.Content is MultipartFormDataContent parts
                ? await parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "request").ReadAsStringAsync(token)
                : await request.Content!.ReadAsStringAsync(token);
            using var dto = JsonDocument.Parse(body); Check(dto.RootElement.GetProperty("userId").GetString() == "canonical", "Setup submitted alias rather than authenticated identity.");
            if (fault == "cancel") cancellation.Cancel(); if (fault == "reset") ClientSessionWork.Reset();
            if (fault == "http") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") };
            return Json(new ModSubmitWorldConfigurationResponseDto { Result = new() { Accepted = fault != "rejected" } });
        }));
        var world = Create(http, env); var context = Context();
        bool success = operation == "submit" ? (await world.SubmitAsync(context, Configuration(), cancellation.Token)).Success
            : (await world.UploadSubstrateAsync(context, "world", new byte[] { 1 }, cancellation.Token)).Success;
        Check(success == (fault is "success" or "rejected") && calls.SequenceEqual(fault == "login-rejected" ? new[] { "login" } : new[] { "login", operation, "logout" })
            && context.AuthToken is null && context.UserId == "alice", "World setup lease lifecycle changed: " + operation + "/" + fault);
    }
    private static async Task Admission()
    {
        ClientSessionWork.Reset(); var env = new Environment(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(new ModPrepareWorldSessionResponseDto { Result = new() { Accepted = true } })); }));
        var world = Create(http, env); var invalid = new ClashOfRimClientNetworkContext("", "", "", null, null, null, null);
        Check(!(await world.HelloAsync(invalid, default)).Success && !(await world.PrepareAsync(invalid, true, default)).Success
            && !(await world.ReadAsync(invalid, true, true, true, default)).Success && !(await world.SubmitAsync(invalid, Configuration(), default)).Success
            && !(await world.SubmitNamesAsync(invalid, "", "", new List<ModWorldFeatureDto>(), default)).Success
            && !(await world.UploadSubstrateAsync(invalid, "", Array.Empty<byte>(), default)).Success && !(await world.DownloadSubstrateAsync(invalid, "", default)).Success
            && calls == 0 && env.Entries == 0, "Unconfigured world operations reached HTTP/providers.");
        var preColony = new ClashOfRimClientNetworkContext("http://localhost", "alice", "", null, null, "pw", null);
        Check((await world.PrepareAsync(preColony, false, default)).Success && (await world.ReadAsync(preColony, true, true, true, default)).Success && calls == 2, "World preparation acquired a colony prerequisite.");
        Check(!(await world.UploadSubstrateAsync(preColony, "world", new byte[] { 1 }, default)).Success && calls == 2, "World writes lost their colony prerequisite.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(!(await world.PrepareAsync(Context(), false, canceled.Token)).Success && calls == 2, "Canceled world preparation sent HTTP.");
    }
    private static ClientWorldProtocol Create(HttpClient http, Environment environment)
    {
        var transport = new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, null), environment);
        var authentication = new ClientSessionProtocol(transport, environment, environment);
        return new(transport, new ClientSetupAuthenticationLease(authentication, transport, environment), environment, environment, () => environment.Entries++);
    }
    private sealed class Environment : IClientProtocolEnvironment, IClientCompatibilityManifestSource, IClientAuthenticationDiagnostics
    {
        internal int Builds, Entries; internal bool ThrowBuild; internal string? Full = "{}";
        internal string CurrentId = "id", CurrentSummary = "summary"; internal IReadOnlyCollection<string>? Requested;
        public string? CurrentLanguage => "zh-CN";
        public string DeserializationFailure(string type) => type;
        public void SessionExpired(string message, string? token) { }
        public void MultipartDisposalFailed(string message) { }
        public string? Id => CurrentId;
        public string? SummaryJson => CurrentSummary;
        public string? BuildJson(IReadOnlyCollection<string>? ids) { Builds++; Requested = ids; if (ThrowBuild) throw new InvalidOperationException("manifest"); return Full; }
        public string NotConfiguredMessage => "not-configured";
        public string ManifestBuildFailedMessage => "manifest-empty";
        public void CleanupFailed(string? code, string? message) { }
    }
    private sealed class Hooks : IDisposable
    {
        private readonly Func<string?>? id = ClashOfRimModNetworkClient.CompatibilityManifestIdProvider;
        private readonly Func<string?>? summary = ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider;
        private readonly Action? log = ClashOfRimModNetworkClient.ServerEntryDiagnosticsLogger;
        private readonly Func<string?>? full = ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider;
        private readonly Func<IReadOnlyCollection<string>?, string?>? selected = ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider;
        internal Hooks() { ClientSessionWork.Reset(); ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = () => "id"; ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = () => "summary"; ClashOfRimModNetworkClient.ServerEntryDiagnosticsLogger = null; ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = () => "{}"; ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = null; }
        public void Dispose() { ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = id; ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = summary; ClashOfRimModNetworkClient.ServerEntryDiagnosticsLogger = log; ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = full; ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = selected; ClientSessionWork.Reset(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(ClientProtocolJson.Serialize(value), Encoding.UTF8, "application/json") };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
