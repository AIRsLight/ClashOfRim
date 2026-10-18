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
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;
using static ClientProtocolTestPorts;

internal static class ClientChatProtocolTests
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
                foreach (bool send in new[] { false, true })
                {
                    foreach (bool facade in new[] { false, true }) foreach (bool defaults in new[] { false, true }) { await Wire(send, facade, defaults); count++; }
                    foreach (string fault in new[] { "http", "rejected", "missing-result", "malformed", "cancel-before", "cancel-response", "generation" }) { await Fault(send, fault); count++; }
                    foreach (string missing in new[] { "server", "user", "colony" }) { await Missing(send, missing); count++; }
                }
            }
            Console.WriteLine($"PASS: chat protocol ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Wire(bool send, bool facade, bool defaults)
    {
        ClientSessionWork.Reset(); int calls = 0; var context = Context(); var env = new ClientProtocolTestPorts.Environment();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base" + ProtocolContractManifest.Find(send ? ProtocolMessageKind.SendChatMessage : ProtocolMessageKind.ListChatMessages).Route, "Chat route/method changed.");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var body = document.RootElement;
            Check(body.GetProperty("userId").GetString() == "用户 İ" && body.GetProperty("colonyId").GetString() == "colony"
                && body.GetProperty("authToken").GetString() == "auth", "Chat borrowed another context or omitted body authentication.");
            Check(body.EnumerateObject().Count() == 5 && !request.Headers.Contains("X-ClashOfRim-AuthToken"), "Chat envelope added fields/headers.");
            if (send) Check(body.GetProperty("targetUserId").GetString() == (defaults ? null : "玩家 İ") && body.GetProperty("text").GetString() == "消息 😀\nline", "Chat text/target was rewritten by transport.");
            else Check(body.GetProperty("afterSequence").GetInt64() == (defaults ? 0 : 123) && body.GetProperty("limit").GetInt32() == (defaults ? 100 : 0), "List cursor clamp or limit contract changed.");
            if (!facade) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(env.CurrentLanguage), "Chat language header changed.");
            return Json(send ? "{\"result\":{\"accepted\":true},\"message\":{\"messageId\":\"id\",\"sequence\":9,\"text\":\"消息 😀\"}}"
                : "{\"result\":{\"accepted\":true},\"messages\":[{\"messageId\":\"id\",\"sequence\":9}],\"latestSequence\":9}");
        }));
        var result = await Invoke(http, context, send, facade, defaults, CancellationToken.None);
        Check(calls == 1 && result.Success && result.Accepted == true && result.Sequence == 9, "Chat response/facade changed.");
    }
    private static async Task Fault(bool send, string fault)
    {
        ClientSessionWork.Reset(); using var cancel = new CancellationTokenSource(); int calls = 0;
        if (fault == "cancel-before") cancel.Cancel();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++; if (fault == "cancel-response") cancel.Cancel(); if (fault == "generation") ClientSessionWork.Reset();
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") }
                : Json(fault == "malformed" ? "{" : fault == "rejected" ? "{\"result\":{\"accepted\":false}}" : "{}"));
        }));
        var result = await Invoke(http, Context(), send, false, false, cancel.Token);
        Check(result.Success == (fault is "rejected" or "missing-result") && result.Accepted != true && calls == (fault == "cancel-before" ? 0 : 1), "Chat fault handling changed: " + fault);
    }
    private static async Task Missing(bool send, string missing)
    {
        ClientSessionWork.Reset(); var context = new ClashOfRimClientNetworkContext(missing == "server" ? "" : "http://localhost", missing == "user" ? "" : "user", missing == "colony" ? "" : "colony", null, null, null, "auth");
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unconfigured chat reached HTTP.")));
        Check((await Invoke(http, context, send, false, false, CancellationToken.None)).Error == "NotConfigured", "Chat configuration guard changed.");
    }
    private static async Task<(bool Success, bool? Accepted, string? Error, long Sequence)> Invoke(HttpClient http, ClashOfRimClientNetworkContext context,
        bool send, bool facade, bool defaults, CancellationToken token)
    {
        var protocol = new ClientChatProtocol(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, null), new ClientProtocolTestPorts.Environment()), () => "not configured");
        var legacy = facade ? new ClashOfRimModNetworkClient(http, context) : null;
        if (send)
        {
            string? target = defaults ? null : "玩家 İ";
            var result = facade ? await legacy!.SendChatMessageAsync(target, "消息 😀\nline", token) : await protocol.SendAsync(context, target, "消息 😀\nline", token);
            return (result.Success, result.Response?.Result?.Accepted, result.ErrorCode, result.Response?.Message?.Sequence ?? 0);
        }
        var read = facade ? defaults ? await legacy!.ListChatMessagesAsync(-4, cancellationToken: token) : await legacy!.ListChatMessagesAsync(123, 0, token)
            : await protocol.ListAsync(context, defaults ? -4 : 123, defaults ? 100 : 0, token);
        return (read.Success, read.Response?.Result?.Accepted, read.ErrorCode, read.Response?.LatestSequence ?? 0);
    }
    private static ClashOfRimClientNetworkContext Context() => new("http://localhost/base", "用户 İ", "colony", "snapshot", null, null, "auth");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
