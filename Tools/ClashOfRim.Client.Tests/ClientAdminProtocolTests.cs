using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientAdminProtocolTests
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
                foreach (string operation in new[] { "read", "save", "action" })
                {
                    foreach (bool legacy in new[] { false, true })
                        foreach (bool defaults in new[] { false, true }) { await Wire(operation, legacy, defaults); count++; }
                    foreach (string fault in new[] { "http", "rejected", "malformed", "missing-result", "cancel-before", "cancel-response", "session-reset" })
                    { await Fault(operation, fault); count++; }
                    foreach (string missing in new[] { "server", "user", "colony" }) { await NotConfigured(operation, missing); count++; }
                }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: administrator protocol ({count} scenarios)");
    }
    private static async Task Wire(string operation, bool legacy, bool defaults)
    {
        ClientSessionWork.Reset(); var env = new Environment(); int calls = 0;
        var context = Context(defaults ? null : "auth-current"); var command = defaults ? new ClientAdminCommand("LockLogin") : Command;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; var kind = Kind(operation);
            Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri(new Uri("http://localhost/base/"), ProtocolContractManifest.Find(kind).Route.TrimStart('/')), "Wrong route/context.");
            Check(request.Content!.Headers.ContentType!.MediaType == "application/json" && !request.Headers.Contains("X-ClashOfRim-AuthToken"), "JSON request changed transport kind.");
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)); var root = document.RootElement;
            Check(root.GetProperty("userId").GetString() == "用户 İ" && root.GetProperty("colonyId").GetString() == "colony-current"
                && root.GetProperty("authToken").GetString() == context.AuthToken, "Wrong authentication/identity mapping.");
            Check(!root.TryGetProperty("protocolVersion", out _) && !root.TryGetProperty("currentSnapshotId", out _), "Added unrelated fields to admin wire contract.");
            if (operation == "save")
            {
                var config = root.GetProperty("configuration");
                Check(config.GetProperty("tradeBaseFeeRate").GetSingle() == 0.13f && config.GetProperty("tradeOrderExpirationDays").GetInt32() == 7,
                    "Culture changed numerical configuration.");
                var mod = config.GetProperty("compatibilityMods")[0];
                Check(mod.GetProperty("packageId").GetString() == "包 İ" && mod.GetProperty("configs")[0].GetProperty("fileName").GetString() == "设置.xml", "Nested configuration lost.");
            }
            if (operation == "action")
            {
                Check(root.GetProperty("actionKind").GetString() == command.Kind && root.GetProperty("targetUserId").GetString() == command.User
                    && root.GetProperty("targetColonyId").GetString() == command.Colony && root.GetProperty("message").GetString() == command.Message
                    && root.GetProperty("notificationSeverity").GetString() == command.Severity && root.GetProperty("persistentNotification").GetBoolean() == command.Persistent,
                    "Command values/defaults were normalized or lost.");
            }
            if (!legacy) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(env.CurrentLanguage), "Language header lost.");
            return Json("{\"result\":{\"accepted\":true}}");
        }));
        IClientAdminTransport transport = legacy ? new Legacy(new ClashOfRimModNetworkClient(http, context))
            : new ClientAdminProtocol(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, "wrong"), env), () => "not configured");
        var result = await Invoke(transport, operation, context, command, CancellationToken.None);
        Check(result.Success && result.Accepted == true && calls == 1, "Public facade/direct protocol did not use the command once.");
    }
    private static async Task Fault(string operation, string fault)
    {
        ClientSessionWork.Reset(); var context = Context("auth"); var env = new Environment(); int calls = 0;
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (fault == "cancel-response") cancel.Cancel();
            if (fault == "session-reset") ClientSessionWork.Reset();
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                : Json(fault switch { "malformed" => "{", "missing-result" => "{}", "rejected" => "{\"result\":{\"accepted\":false,\"errorCode\":17,\"message\":\"rejected\"}}", _ => "{\"result\":{\"accepted\":true}}" }));
        }));
        var transport = new ClientAdminProtocol(new(http, context, env), () => "not configured");
        if (fault == "cancel-before") cancel.Cancel();
        var result = await Invoke(transport, operation, context, Command, cancel.Token);
        Check(result.Success == (fault is "rejected" or "missing-result") && result.Accepted != true && calls == (fault == "cancel-before" ? 0 : 1), "Wrong error/cancellation semantics: " + fault);
        if (fault == "http") Check(result.Error == "HttpError", "HTTP error code changed.");
    }
    private static async Task NotConfigured(string operation, string missing)
    {
        ClientSessionWork.Reset(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var context = new ClashOfRimClientNetworkContext(missing == "server" ? "" : "http://localhost", missing == "user" ? "" : "alice", missing == "colony" ? "" : "colony", null, null, null, "auth");
        var result = await Invoke(new ClientAdminProtocol(new(http, context, new Environment()), () => "not configured"), operation, context, Command, CancellationToken.None);
        Check(!result.Success && result.Error == "NotConfigured" && calls == 0, "Incomplete context reached transport.");
    }
    private static readonly ClientAdminCommand Command = new("Custom ACTION İ", "目标", "colony-target", "消息 العربية", "Critical", false);
    private static ClashOfRimClientNetworkContext Context(string? auth) => new("http://localhost/base", "用户 İ", "colony-current", "snapshot", null, null, auth);
    private static ProtocolMessageKind Kind(string operation) => operation switch { "read" => ProtocolMessageKind.AdminStatus, "save" => ProtocolMessageKind.AdminUpdateConfiguration, _ => ProtocolMessageKind.AdminAction };
    private static async Task<(bool Success, bool? Accepted, string? Error)> Invoke(IClientAdminTransport transport, string operation, ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token)
    {
        if (operation == "read") { var r = await transport.ReadAsync(context, token); return (r.Success, r.Response?.Result?.Accepted, r.ErrorCode); }
        if (operation == "save")
        {
            var r = await transport.SaveAsync(context, new() { TradeOrderExpirationDays = 7, TradeBaseFeeRate = 0.13f,
                CompatibilityMods = new() { new() { PackageId = "包 İ", Configs = new() { new() { FileName = "设置.xml", Mode = "Enforce" } } } } }, token);
            return (r.Success, r.Response?.Result?.Accepted, r.ErrorCode);
        }
        var action = await transport.ActAsync(context, command, token); return (action.Success, action.Response?.Result?.Accepted, action.ErrorCode);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
        public string DeserializationFailure(string responseType) => responseType;
        public void SessionExpired(string message, string? authToken) => throw new InvalidOperationException("Unexpected session expiry.");
        public void MultipartDisposalFailed(string message) => throw new InvalidOperationException("Admin used multipart.");
    }
    private sealed record Legacy(ClashOfRimModNetworkClient Client) : IClientAdminTransport
    {
        public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token) => Client.AdminStatusAsync(token);
        public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto configuration, CancellationToken token) => Client.AdminUpdateConfigurationAsync(configuration, token);
        public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token)
            => Client.AdminActionAsync(command.Kind, command.User, command.Colony, command.Message, command.Severity, command.Persistent, token);
    }
}
