using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

internal static class AdministratorSetupTransportTests
{
    public static void Run()
    {
        foreach (string operation in new[] { "requirements", "baseline", "world", "upload" })
        foreach (string? existing in new[] { null, "existing-token" })
        foreach (string outcome in new[] { "success", "canonical", "rejected", "http-error", "exception", "cancel", "session-cancel" })
            Check(operation, existing, outcome).GetAwaiter().GetResult();
        foreach (string operation in new[] { "requirements", "baseline", "world", "upload" })
        foreach (string outcome in new[] { "login-rejected", "login-no-token", "login-no-session" })
            Check(operation, null, outcome).GetAwaiter().GetResult();
    }

    private static async Task Check(string operation, string? existing, string outcome)
    {
        ClientSessionWork.Reset();
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler(operation, existing, outcome, cancel);
        using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "admin", "colony", null, null, "password", existing);
        var client = new ClashOfRimModNetworkClient(http, context);
        var baseline = new ModSubmitAdminBaselineRequestDto
            { GeneratedAtUtc = "2026-10-17T00:00:00Z", UserId = "caller", ColonyId = "caller-colony", AuthToken = "caller-token" };
        bool success = false;
        try
        {
            await ClientSessionWork.Run(async () =>
            {
                if (operation == "upload")
                {
                    var result = await client.UploadWorldSubstrateAsync("world", new byte[] { 1, 2, 3 }, cancel.Token);
                    success = result.Success && result.Response?.Result?.Accepted == true;
                }
                else if (operation == "world")
                {
                    var result = await client.SubmitWorldConfigurationAsync(new ModWorldConfigurationDto
                        { WorldConfigurationId = "world", SeedString = "seed", ConfiguredAtUtc = DateTimeOffset.UtcNow.ToString("O") }, cancel.Token);
                    success = result.Success && result.Response?.Result?.Accepted == true;
                }
                else if (operation == "baseline")
                {
                    var result = await client.SubmitAdminBaselineAsync(baseline, cancel.Token);
                    success = result.Success && result.Response?.Result?.Accepted == true;
                }
                else
                {
                    var result = await client.GetAdminBaselineRequirementsAsync(cancel.Token);
                    success = result.Success && result.Response?.Result?.Accepted == true;
                }
            });
        }
        finally { ClientSessionWork.Reset(); }
        Require(success == (outcome is "success" or "canonical"), "Unexpected setup transport outcome: " + outcome);
        string[] expected = existing is not null ? new[] { "business" } : outcome.StartsWith("login-") ? new[] { "login" } : new[] { "login", "business", "logout" };
        Require(handler.Calls.SequenceEqual(expected), "Wrong setup session sequence: " + string.Join(",", handler.Calls));
        Require(handler.LoggedOut == expected.Contains("logout"), "Temporary session cleanup was cancelled before completion.");
        Require(context.AuthToken == existing, "Private setup credentials leaked into caller context.");
        Require(baseline.UserId == "caller" && baseline.ColonyId == "caller-colony" && baseline.AuthToken == "caller-token",
            "Authentication mutated the caller's reusable baseline request.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly bool submit, world, upload;
        private readonly string token, outcome, userId;
        private readonly CancellationTokenSource cancel;
        public readonly List<string> Calls = new();
        public bool LoggedOut;
        public Handler(string operation, string? existing, string outcome, CancellationTokenSource cancel)
        {
            submit = operation == "baseline"; world = operation == "world"; upload = operation == "upload";
            token = existing ?? "setup-token"; this.outcome = outcome; this.cancel = cancel;
            userId = existing is null && outcome == "canonical" ? "canonical-admin" : "admin";
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string route = request.RequestUri!.AbsolutePath;
            if (route == ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route)
            {
                Calls.Add("login");
                return Json(outcome switch
                {
                    "login-rejected" => "{\"result\":{\"accepted\":false}}",
                    "login-no-token" => "{\"result\":{\"accepted\":true},\"sessionId\":\"setup-session\"}",
                    "login-no-session" => "{\"result\":{\"accepted\":true},\"authToken\":\"setup-token\"}",
                    "canonical" => "{\"result\":{\"accepted\":true},\"authToken\":\"setup-token\",\"sessionId\":\"setup-session\",\"authenticatedUserId\":\" canonical-admin \"}",
                    _ => "{\"result\":{\"accepted\":true},\"authToken\":\"setup-token\",\"sessionId\":\"setup-session\"}"
                });
            }
            HttpContent content = request.Content!;
            if (upload && route == ProtocolContractManifest.Find(ProtocolMessageKind.UploadWorldSubstrate).Route)
                content = ((MultipartFormDataContent)content).First();
            string body = await content.ReadAsStringAsync(cancellationToken);
            if (route == ProtocolContractManifest.Find(upload ? ProtocolMessageKind.UploadWorldSubstrate : world ? ProtocolMessageKind.SubmitWorldConfiguration : submit ? ProtocolMessageKind.SubmitAdminBaseline : ProtocolMessageKind.GetAdminBaselineRequirements).Route)
            {
                Calls.Add("business");
                // Deserialize the Mod's real DataContract JSON with the server's actual request type.
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                if (upload)
                {
                    using var dto = JsonDocument.Parse(body);
                    Require(dto.RootElement.GetProperty("userId").GetString() == userId
                        && dto.RootElement.GetProperty("colonyId").GetString() == "colony"
                        && dto.RootElement.GetProperty("worldConfigurationId").GetString() == "world"
                        && request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == token,
                        "World substrate identity/token/body contract differs.");
                    var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                    Require(parts.Length == 2 && (await parts[1].ReadAsByteArrayAsync(cancellationToken)).SequenceEqual(new byte[] { 1, 2, 3 }), "World substrate payload changed.");
                }
                else if (world)
                {
                    var dto = JsonSerializer.Deserialize<SubmitWorldConfigurationRequest>(body, options)!;
                    Require(dto.UserId == userId && dto.ColonyId == "colony" && dto.AuthToken == token && dto.Configuration.SeedString == "seed", "World configuration identity/token/body contract differs.");
                }
                else if (submit)
                {
                    var dto = JsonSerializer.Deserialize<SubmitAdminBaselineRequest>(body, options)!;
                    Require(dto.UserId == userId && dto.ColonyId == "colony" && dto.AuthToken == token
                        && dto.GeneratedAtUtc == new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), "Submission identity/token/body contract differs.");
                }
                else
                {
                    var dto = JsonSerializer.Deserialize<GetAdminBaselineRequirementsRequest>(body, options)!;
                    Require(dto.UserId == userId && dto.ColonyId == "colony" && dto.AuthToken == token, "Requirements identity/token contract differs.");
                }
                if (outcome == "http-error") return new(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") };
                if (outcome == "exception") throw new HttpRequestException("transport failed");
                if (outcome == "cancel") { cancel.Cancel(); throw new TaskCanceledException(); }
                if (outcome == "session-cancel") { ClientSessionWork.Reset(); throw new TaskCanceledException(); }
                return Json(outcome == "rejected" ? "{\"result\":{\"accepted\":false}}" : "{\"result\":{\"accepted\":true}}");
            }
            if (route == ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route)
            {
                Calls.Add("logout"); cancellationToken.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(body);
                Require(json.RootElement.GetProperty("sessionId").GetString() == "setup-session"
                    && json.RootElement.GetProperty("userId").GetString() == userId
                    && json.RootElement.GetProperty("colonyId").GetString() == "colony", "Cleanup released the wrong session.");
                LoggedOut = true;
                return Json("{\"result\":{\"accepted\":true}}");
            }
            throw new Exception("Unexpected route: " + route);
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
