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
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientAdminBaselineProtocolTests
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
                foreach (bool submit in new[] { false, true })
                {
                    foreach (bool legacy in new[] { false, true })
                    foreach (bool temporary in new[] { false, true }) { await Exchange(submit, temporary, legacy, "success"); count++; }
                    foreach (bool temporary in new[] { false, true })
                    foreach (string fault in new[] { "http", "rejected", "missing-result", "malformed", "cancel-before", "cancel-response", "reset" })
                    { await Exchange(submit, temporary, false, fault); count++; }
                    await Exchange(submit, true, false, "login-rejected"); count++;
                    foreach (string missing in new[] { "server", "user", "colony" }) { await Missing(submit, missing); count++; }
                }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: administrator baseline protocol ({count} scenarios)");
    }
    private static async Task Exchange(bool submit, bool temporary, bool legacy, string fault)
    {
        ClientSessionWork.Reset(); var env = new Environment(); using var cancellation = new CancellationTokenSource();
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户 İ", "colony", "snapshot", "ticket", "password", temporary ? null : "borrowed");
        var source = Baseline(); var frozen = ClientProtocolJson.Copy(source); var calls = new List<ProtocolMessageKind>();
        string requestUser = temporary ? "canonical" : context.UserId; string requestAuth = temporary ? "temporary" : "borrowed";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route, StringComparison.Ordinal) ? ProtocolMessageKind.Login
                : path.EndsWith(ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, StringComparison.Ordinal) ? ProtocolMessageKind.Logout
                : submit ? ProtocolMessageKind.SubmitAdminBaseline : ProtocolMessageKind.GetAdminBaselineRequirements;
            calls.Add(kind);
            Check(request.RequestUri == new Uri(new Uri("http://localhost/base/"), ProtocolContractManifest.Find(kind).Route.TrimStart('/')), "Baseline used wrong route or transport context.");
            Check(request.Method == HttpMethod.Post && request.Content!.Headers.ContentType!.MediaType == "application/json"
                && !request.Headers.Contains(MultipartSnapshotTransport.AuthenticationHeaderName), "Baseline acquired a multipart/header contract.");
            string body = await request.Content.ReadAsStringAsync(token); using var document = JsonDocument.Parse(body); var dto = document.RootElement;
            if (kind == ProtocolMessageKind.Login)
            {
                Check(dto.GetProperty("userId").GetString() == context.UserId, "Temporary login lost captured identity.");
                source.StandardMarketValues[0].MarketValue = 999; source.BaselineExtensions[0].Records[0].Values["label"] = "changed";
                source.StuffMarketValues.Clear(); source.GeneratedAtUtc = "changed";
                return Json(new ModLoginResponseDto { Result = new() { Accepted = fault != "login-rejected" }, AuthenticatedUserId = "canonical", AuthToken = "temporary", SessionId = "setup" });
            }
            Check(dto.GetProperty("userId").GetString() == requestUser && dto.GetProperty("colonyId").GetString() == "colony", "Baseline/cleanup used wrong identity.");
            if (kind == ProtocolMessageKind.Logout)
            {
                Check(!token.IsCancellationRequested && dto.GetProperty("sessionId").GetString() == "setup", "Temporary cleanup borrowed canceled work or lost the exact session.");
                return Json(new ModLogoutResponseDto { Result = new() { Accepted = true } });
            }
            Check(dto.GetProperty("authToken").GetString() == requestAuth, "Baseline used wrong credential.");
            Check(dto.GetProperty("protocolVersion").GetString() == (submit ? frozen.ProtocolVersion : ClashOfRimVersion.ProtocolVersion), "Protocol version was normalized/lost.");
            Check(!dto.TryGetProperty("currentSnapshotId", out _), "Baseline acquired unrelated snapshot identity.");
            if (submit)
            {
                var actual = ClientProtocolJson.Deserialize<ModSubmitAdminBaselineRequestDto>(body, type => type);
                Check(ClientProtocolJson.Serialize(actual) == ClientProtocolJson.Serialize(frozen.WithAuthentication(requestUser, "colony", requestAuth)),
                    "Baseline changed or borrowed mutable nested data while logging in.");
            }
            if (fault == "cancel-response") cancellation.Cancel();
            if (fault == "reset") ClientSessionWork.Reset();
            if (fault == "http") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") };
            if (fault == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("invalid-json") };
            var result = fault == "missing-result" ? null : new ModProtocolResponseDto { Accepted = fault != "rejected" };
            return submit ? Json(new ModSubmitAdminBaselineResponseDto { Result = result, StandardMarketValueCount = 12, TrapCandidateCount = 3 })
                : Json(new ModGetAdminBaselineRequirementsResponseDto { Result = result, BaselineExtensions = new() { new() { ProviderId = "provider", Kind = "kind", DisplayName = "中文 İ" } } });
        }));
        if (fault == "cancel-before") cancellation.Cancel();
        var protocol = Create(http, env); var facade = new ClashOfRimModNetworkClient(http, context);
        bool success; bool? accepted;
        if (submit)
        {
            var response = legacy ? await facade.SubmitAdminBaselineAsync(source, cancellation.Token) : await protocol.SubmitAsync(context, source, cancellation.Token);
            success = response.Success; accepted = response.Response?.Result?.Accepted;
            if (fault == "success") Check(response.Response?.StandardMarketValueCount == 12 && response.Response.TrapCandidateCount == 3, "Submit response lost counts.");
        }
        else
        {
            var response = legacy ? await facade.GetAdminBaselineRequirementsAsync(cancellation.Token) : await protocol.RequirementsAsync(context, cancellation.Token);
            success = response.Success; accepted = response.Response?.Result?.Accepted;
            if (fault == "success") Check(response.Response?.BaselineExtensions.Single().DisplayName == "中文 İ", "Requirements response lost extensions.");
        }
        Check(success == (fault is "success" or "rejected" or "missing-result") && accepted == (fault == "success" ? true : fault == "rejected" ? false : (bool?)null), "Network/acceptance semantics changed: " + fault);
        var operation = submit ? ProtocolMessageKind.SubmitAdminBaseline : ProtocolMessageKind.GetAdminBaselineRequirements;
        var expected = fault == "cancel-before" ? Array.Empty<ProtocolMessageKind>() : fault == "login-rejected" ? new[] { ProtocolMessageKind.Login }
            : temporary ? new[] { ProtocolMessageKind.Login, operation, ProtocolMessageKind.Logout } : new[] { operation };
        Check(calls.SequenceEqual(expected) && context.AuthToken == (temporary ? null : "borrowed") && source.UserId == "untrusted",
            "Baseline leaked/overwrote a credential or changed lease cleanup order.");
    }
    private static async Task Missing(bool submit, string missing)
    {
        ClientSessionWork.Reset(); var env = new Environment(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new Exception("Unexpected HTTP."); }));
        var context = new ClashOfRimClientNetworkContext(missing == "server" ? "" : "http://localhost", missing == "user" ? "" : "alice", missing == "colony" ? "" : "colony", null, null, null, null);
        var protocol = Create(http, env);
        string? code = submit ? (await protocol.SubmitAsync(context, Baseline(), default)).ErrorCode : (await protocol.RequirementsAsync(context, default)).ErrorCode;
        Check(code == "NotConfigured" && calls == 0, "Incomplete baseline identity reached login/HTTP.");
    }
    private static ClientAdminBaselineProtocol Create(HttpClient http, Environment env)
    {
        var transport = new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, null), env);
        return new(new ClientSetupAuthenticationLease(new ClientSessionProtocol(transport, env, env), transport, env), () => env.NotConfiguredMessage);
    }
    private static ModSubmitAdminBaselineRequestDto Baseline() => new()
    {
        ProtocolVersion = "explicit-version", UserId = "untrusted", ColonyId = "untrusted", AuthToken = "untrusted", GeneratedAtUtc = "2026-10-18T00:00:00.0000000+00:00",
        StandardMarketValues = new() { new() { DefName = "thing", MarketValue = 1.25f } },
        StuffMarketValues = new() { new() { ThingDefName = "thing", StuffDefName = "stuff", MarketValue = 3.75f } },
        QualityMarketValueModifiers = new() { new() { Quality = "Normal", Factor = 1.2f, MaxGain = 10.5f } },
        PackableBuildings = new() { new() { DefName = "building", MinifiedDefName = "packed" } },
        Buildings = new() { new() { DefName = "building", UseHitPoints = true, EstimatedMaxHitPoints = 123, Minifiable = true } },
        StuffHitPointModifiers = new() { new() { DefName = "stuff", MaxHitPointsFactor = 1.3f, MaxHitPointsOffset = -5.5f } },
        TrapClassifications = new() { new() { DefName = "trap", ScanStatus = "CandidateRequiresApproval", AdminApproved = false, InheritsBuildingTrap = false } },
        BaselineExtensions = new() { new() { ProviderId = "provider", Kind = "kind", Records = new() { new() { Key = "record", Values = new() { ["label"] = "中文 İ", ["ratio"] = "0.3" } } } } }
    };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(ClientProtocolJson.Serialize(value), Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Environment : IClientProtocolEnvironment, IClientCompatibilityManifestSource, IClientAuthenticationDiagnostics
    {
        public string CurrentLanguage => "zh-CN";
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) => throw new Exception("Unexpected multipart.");
        public string Id => "id";
        public string SummaryJson => "summary";
        public string BuildJson(IReadOnlyCollection<string>? ids) => "{}";
        public string NotConfiguredMessage => "not-configured";
        public string ManifestBuildFailedMessage => "manifest-empty";
        public void CleanupFailed(string? code, string? message) => throw new Exception("Unexpected cleanup failure.");
    }
}
