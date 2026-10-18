using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientColonyProtocolTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var kind in Kinds)
                {
                    foreach (bool legacy in new[] { false, true })
                        foreach (bool defaults in new[] { false, true }) { await Wire(kind, legacy, defaults); count++; }
                    foreach (string fault in new[] { "http", "reject", "missing-result", "malformed", "cancel-before", "cancel-response", "reset" }) { await Fault(kind, fault); count++; }
                    foreach (string missing in new[] { "server", "user", "colony" }) { await Missing(kind, missing); count++; }
                }
            }
            Console.WriteLine($"  colony protocol: {count} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly ProtocolMessageKind[] Kinds = { ProtocolMessageKind.RegisterPlayerColonySites, ProtocolMessageKind.PreflightColonyRelocation, ProtocolMessageKind.ConfirmColonyRelocation, ProtocolMessageKind.AbandonPlayerColony };
    private static async Task Wire(ProtocolMessageKind kind, bool legacy, bool defaults)
    {
        ClientSessionWork.Reset(); var context = Context(); int calls = 0; var env = new Environment();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri(new Uri(context.ServerBaseUrl + "/"), ProtocolContractManifest.Find(kind).Route.TrimStart('/')), "Wrong route/context.");
            Check(request.Content!.Headers.ContentType!.MediaType == "application/json" && !request.Headers.Contains("X-ClashOfRim-AuthToken"), "Wrong transport envelope.");
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)); var root = document.RootElement;
            Check(root.GetProperty("userId").GetString() == context.UserId && root.GetProperty("colonyId").GetString() == context.ColonyId
                && root.GetProperty("authToken").GetString() == context.AuthToken && root.GetProperty("protocolVersion").GetString() == ClashOfRimVersion.ProtocolVersion, "Identity/version lost.");
            if (kind == ProtocolMessageKind.RegisterPlayerColonySites)
            {
                Check(root.GetProperty("sites").GetArrayLength() == (defaults ? 0 : 1) && root.GetProperty("extensions").GetArrayLength() == (defaults ? 0 : 1), "Registration defaults changed.");
                Check(root.GetProperty("suppressWorldConfigurationNotification").GetBoolean() == !defaults, "Notification suppression changed.");
                if (!defaults)
                {
                    var site = root.GetProperty("sites")[0]; var extension = root.GetProperty("extensions")[0];
                    Check(site.GetProperty("tileLayerId").GetInt32() == 7 && site.GetProperty("appearance").GetProperty("colorHex").GetString() == "#123456"
                        && site.GetProperty("label").GetString() == "基地 İ", "Nested site fields changed.");
                    Check(extension.GetProperty("payloadJson").GetString() == "{\"value\":1.25,\"name\":\"文化\"}", "Extension payload normalized.");
                }
            }
            else
            {
                Check(root.GetProperty("idempotencyKey").GetString() == "key:Î|1", "Idempotency key changed.");
                if (kind != ProtocolMessageKind.ConfirmColonyRelocation) Check(root.GetProperty("currentSnapshotId").GetString() == "base", "Captured snapshot changed.");
                if (kind != ProtocolMessageKind.AbandonPlayerColony)
                    Check(root.GetProperty("targetTile").GetInt32() == -3 && root.GetProperty("targetTileLayerId").GetInt32() == (defaults ? 0 : 7), "Relocation coordinates/default normalization changed.");
                if (kind == ProtocolMessageKind.ConfirmColonyRelocation)
                    Check(root.GetProperty("previousSnapshotId").GetString() == (defaults ? "" : "previous") && root.GetProperty("relocatedSnapshotId").GetString() == (defaults ? "" : "relocated"), "Relocation snapshot fields changed.");
            }
            if (!legacy) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(env.CurrentLanguage), "Language header changed.");
            return Json("{\"result\":{\"accepted\":true}}");
        }));
        var result = await Invoke(http, env, kind, context, legacy, defaults, CancellationToken.None);
        Check(result.Success && result.Accepted == true && calls == 1, "Direct protocol/facade failed.");
    }
    private static async Task Fault(ProtocolMessageKind kind, string fault)
    {
        ClientSessionWork.Reset(); using var cancel = new CancellationTokenSource(); int calls = 0;
        if (fault == "cancel-before") cancel.Cancel();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++; if (fault == "cancel-response") cancel.Cancel(); if (fault == "reset") ClientSessionWork.Reset();
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") }
                : Json(fault switch { "reject" => "{\"result\":{\"accepted\":false}}", "missing-result" => "{}", "malformed" => "{", _ => "{\"result\":{\"accepted\":true}}" }));
        }));
        var result = await Invoke(http, new Environment(), kind, Context(), false, false, cancel.Token);
        Check(result.Success == (fault is "reject" or "missing-result") && result.Accepted != true, "Fault became accepted: " + fault);
        Check(calls == (fault == "cancel-before" ? 0 : 1), "Cancelled call reached HTTP.");
    }
    private static async Task Missing(ProtocolMessageKind kind, string missing)
    {
        ClientSessionWork.Reset(); var context = new ClashOfRimClientNetworkContext(missing == "server" ? "" : "http://localhost/base", missing == "user" ? "" : "user", missing == "colony" ? "" : "colony", "base", null, null, "auth");
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unconfigured request reached HTTP.")));
        var result = await Invoke(http, new Environment(), kind, context, false, false, CancellationToken.None);
        Check(!result.Success && result.Error == "NotConfigured", "Missing identity not rejected.");
    }
    private static async Task<(bool Success, bool? Accepted, string? Error)> Invoke(HttpClient http, Environment env, ProtocolMessageKind kind,
        ClashOfRimClientNetworkContext context, bool legacy, bool defaults, CancellationToken token)
    {
        var protocol = new ClientColonyProtocol(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, "wrong"), env), () => "not configured");
        var facade = legacy ? new ClashOfRimModNetworkClient(http, context) : null;
        if (kind == ProtocolMessageKind.RegisterPlayerColonySites)
        {
            var sites = defaults ? null! : new List<ModPlayerColonySiteDto> { new() { Tile = 10, TileLayerId = 7, Label = "基地 İ", Appearance = new() { ColorHex = "#123456" } } };
            var extensions = defaults ? null : new List<ModWorldConfigurationExtensionDto> { new() { ProviderId = "provider", Kind = "ideo", SchemaVersion = "1", PayloadJson = "{\"value\":1.25,\"name\":\"文化\"}" } };
            var result = legacy ? await facade!.RegisterPlayerColonySitesAsync(sites, extensions, !defaults, token) : await protocol.RegisterAsync(context, sites, extensions, !defaults, token);
            return (result.Success, result.Response?.Result?.Accepted, result.ErrorCode);
        }
        if (kind == ProtocolMessageKind.AbandonPlayerColony)
        {
            var result = legacy ? await facade!.AbandonPlayerColonyAsync("key:Î|1", token) : await protocol.AbandonAsync(context, "key:Î|1", token);
            return (result.Success, result.Response?.Result?.Accepted, result.ErrorCode);
        }
        string previous = defaults ? null! : "previous", relocated = defaults ? null! : "relocated"; int layer = defaults ? -2 : 7;
        var relocation = kind == ProtocolMessageKind.PreflightColonyRelocation
            ? legacy ? await facade!.PreflightColonyRelocationAsync(-3, "key:Î|1", layer, token) : await protocol.PreflightAsync(context, -3, "key:Î|1", layer, token)
            : legacy ? await facade!.ConfirmColonyRelocationAsync(previous, relocated, -3, "key:Î|1", layer, token) : await protocol.ConfirmAsync(context, previous, relocated, -3, "key:Î|1", layer, token);
        return (relocation.Success, relocation.Response?.Result?.Accepted, relocation.ErrorCode);
    }
    private static ClashOfRimClientNetworkContext Context() => new("http://localhost/base", "用户 İ", "colony", "base", null, null, "auth");
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string CurrentLanguage => "简体中文 İ";
        public string DeserializationFailure(string type) => type;
        public void SessionExpired(string message, string? authToken) => throw new InvalidOperationException("Unexpected session expiry.");
        public void MultipartDisposalFailed(string message) => throw new InvalidOperationException("Unexpected multipart.");
    }
}
