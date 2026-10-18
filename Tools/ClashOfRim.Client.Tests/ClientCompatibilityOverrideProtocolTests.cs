using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;
using static ClientProtocolTestPorts;

internal static class ClientCompatibilityOverrideProtocolTests
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
                foreach (bool facade in new[] { false, true })
                {
                    foreach (string? ticket in new string?[] { null, " ", "steam-ticket" })
                        foreach (bool menu in new[] { false, true }) { await Wire(facade, ticket, menu); count++; }
                    foreach (string fault in new[] { "http", "reject", "missing", "malformed", "cancel-before", "cancel-response", "generation" })
                    { await Fault(facade, fault); count++; }
                    foreach (bool missingServer in new[] { false, true }) { await Missing(facade, missingServer); count++; }
                }
            }
            Console.WriteLine($"  compatibility override protocol: {count} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Wire(bool facade, string? ticket, bool menu)
    {
        ClientSessionWork.Reset(); int calls = 0;
        var context = new ClashOfRimClientNetworkContext("http://localhost/base", "用户 İ", menu ? "" : "colony", "snapshot", ticket, "秘密 Î", menu ? "" : "auth");
        string? manifest = menu ? null : "{\"language\":\"简体中文 İ\",\"value\":1.25}";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base" + ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route, "Wrong endpoint.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var body = json.RootElement;
            Check(body.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "colonyId", "compatibilityManifestJson", "password", "steamAuthTicket", "userId" }), "Override wire fields changed.");
            Check(body.GetProperty("userId").GetString() == context.UserId && body.GetProperty("colonyId").ValueKind == JsonValueKind.Null
                && body.GetProperty("password").GetString() == context.OfflinePassword && body.GetProperty("steamAuthTicket").GetString() == (string.IsNullOrWhiteSpace(ticket) ? context.UserId : ticket)
                && body.GetProperty("compatibilityManifestJson").GetString() == (manifest ?? ""), "Credential or manifest mapping changed.");
            Check(!request.Headers.Contains("X-ClashOfRim-AuthToken"), "Credential command acquired a session header.");
            if (!facade) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(new ClientProtocolTestPorts.Environment().CurrentLanguage), "Language header lost.");
            return Json("{\"result\":{\"accepted\":true},\"serverCompatibilityManifestJson\":\"accepted\",\"updatedAtUtc\":\"2026-10-19T00:00:00Z\"}");
        }));
        var result = await Invoke(http, context, facade, manifest!, CancellationToken.None);
        Check(calls == 1 && result.Success && result.Response?.Result?.Accepted == true && result.Response.ServerCompatibilityManifestJson == "accepted", "Result changed.");
    }
    private static async Task Fault(bool facade, string fault)
    {
        ClientSessionWork.Reset(); using var cancel = new CancellationTokenSource(); int calls = 0;
        if (fault == "cancel-before") cancel.Cancel();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++; if (fault == "cancel-response") cancel.Cancel(); if (fault == "generation") ClientSessionWork.Reset();
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failure") }
                : Json(fault == "malformed" ? "{" : fault == "reject" ? "{\"result\":{\"accepted\":false}}" : "{}"));
        }));
        var result = await Invoke(http, new("http://localhost", "alice", "", null, null, null, null), facade, "manifest", cancel.Token);
        Check(result.Success == (fault is "reject" or "missing") && result.Response?.Result?.Accepted != true && calls == (fault == "cancel-before" ? 0 : 1), "Failure policy changed: " + fault);
    }
    private static async Task Missing(bool facade, bool server)
    {
        ClientSessionWork.Reset(); using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Missing credentials reached HTTP.")));
        var context = new ClashOfRimClientNetworkContext(server ? "" : "http://localhost", server ? "alice" : " ", "", null, null, null, null);
        Check((await Invoke(http, context, facade, "manifest", CancellationToken.None)).ErrorCode == "NotConfigured", "Missing account admitted.");
    }
    private static Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> Invoke(HttpClient http, ClashOfRimClientNetworkContext context, bool facade, string manifest, CancellationToken token)
        => facade ? new ClashOfRimModNetworkClient(http, context).OverrideCompatibilityBaselineAsync(manifest, token)
            : new ClientCompatibilityOverrideProtocol(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, null), new ClientProtocolTestPorts.Environment()), () => "not configured").SubmitAsync(context, manifest, token);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
