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

internal static class AdminBaselineTransportTests
{
    public static void Run()
    {
        foreach (bool submit in new[] { false, true })
        foreach (string? existing in new[] { null, "existing-token" })
        foreach (string outcome in new[] { "success", "rejected", "http-error", "exception", "cancel", "session-cancel" })
            Check(submit, existing, outcome).GetAwaiter().GetResult();
        foreach (bool submit in new[] { false, true })
        foreach (string outcome in new[] { "login-rejected", "login-no-token", "login-no-session" })
            Check(submit, null, outcome).GetAwaiter().GetResult();
    }

    private static async Task Check(bool submit, string? existing, string outcome)
    {
        ClientSessionWork.Reset();
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler(submit, existing, outcome, cancel);
        using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://127.0.0.1:12345", "admin", "colony", null, null, "password", existing);
        var client = new ClashOfRimModNetworkClient(http, context);
        bool success = false;
        try
        {
            await ClientSessionWork.Run(async () =>
            {
                if (submit)
                {
                    var result = await client.SubmitAdminBaselineAsync(new ModSubmitAdminBaselineRequestDto { GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O") }, cancel.Token);
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
        Require(success == (outcome == "success"), "Unexpected baseline transport outcome: " + outcome);
        string[] expected = existing is not null ? new[] { "business" } : outcome.StartsWith("login-") ? new[] { "login" } : new[] { "login", "business", "logout" };
        Require(handler.Calls.SequenceEqual(expected), "Wrong baseline session sequence: " + string.Join(",", handler.Calls));
        Require(handler.LoggedOut == expected.Contains("logout"), "Temporary session cleanup was cancelled before completion.");
        Require(context.AuthToken == existing, "Private setup credentials leaked into caller context.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly bool submit;
        private readonly string token, outcome;
        private readonly CancellationTokenSource cancel;
        public readonly List<string> Calls = new();
        public bool LoggedOut;
        public Handler(bool submit, string? existing, string outcome, CancellationTokenSource cancel)
        { this.submit = submit; token = existing ?? "setup-token"; this.outcome = outcome; this.cancel = cancel; }
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
                    _ => "{\"result\":{\"accepted\":true},\"authToken\":\"setup-token\",\"sessionId\":\"setup-session\"}"
                });
            }
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (route == ProtocolContractManifest.Find(submit ? ProtocolMessageKind.SubmitAdminBaseline : ProtocolMessageKind.GetAdminBaselineRequirements).Route)
            {
                Calls.Add("business");
                // Deserialize the Mod's real DataContract JSON with the server's actual request type.
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                if (submit)
                {
                    var dto = JsonSerializer.Deserialize<SubmitAdminBaselineRequest>(body, options)!;
                    Require(dto.UserId == "admin" && dto.ColonyId == "colony" && dto.AuthToken == token, "Submission identity/token contract differs.");
                }
                else
                {
                    var dto = JsonSerializer.Deserialize<GetAdminBaselineRequirementsRequest>(body, options)!;
                    Require(dto.UserId == "admin" && dto.ColonyId == "colony" && dto.AuthToken == token, "Requirements identity/token contract differs.");
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
                    && json.RootElement.GetProperty("userId").GetString() == "admin"
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
