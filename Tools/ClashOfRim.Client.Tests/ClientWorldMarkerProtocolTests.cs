using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Protocol;
using static ClientProtocolTestPorts;

internal static class ClientWorldMarkerProtocolTests
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
                foreach (bool write in new[] { false, true })
                {
                    foreach (bool legacy in new[] { false, true }) foreach (bool defaults in new[] { false, true })
                    { await Wire(write, legacy, defaults); count++; }
                    foreach (string fault in new[] { "http", "reject", "missing-result", "malformed", "cancel-before", "cancel-response", "generation" })
                    { await Fault(write, fault); count++; }
                    foreach (string missing in new[] { "server", "user", "colony" }) { await Missing(write, missing); count++; }
                }
            }
            Console.WriteLine($"  world marker protocol: {count} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Wire(bool write, bool legacy, bool defaults)
    {
        ClientSessionWork.Reset(); int calls = 0; var env = new ClientProtocolTestPorts.Environment(); var context = Context();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/base" + ProtocolContractManifest.Find(write ? ProtocolMessageKind.SyncRuntimeWorldObjects : ProtocolMessageKind.SyncWorldMapMarkers).Route, "Wrong route/method.");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = doc.RootElement;
            Check(root.GetProperty("userId").GetString() == context.UserId && root.GetProperty("colonyId").GetString() == context.ColonyId, "Captured context not used.");
            Check(!root.TryGetProperty("protocolVersion", out _) && !request.Headers.Contains("X-ClashOfRim-AuthToken"), "Legacy envelope changed.");
            Check(DateTimeOffset.TryParseExact(root.GetProperty(write ? "sentAtUtc" : "knownAtUtc").GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                && Math.Abs((timestamp - DateTimeOffset.UtcNow).TotalSeconds) < 30, "Invalid UTC timestamp.");
            if (write)
            {
                Check(root.GetProperty("authToken").GetString() == "auth" && root.GetProperty("snapshotId").GetString() == "base", "Runtime write lost credentials/snapshot.");
                var objects = root.GetProperty("objects"); Check(objects.GetArrayLength() == (defaults ? 0 : 1), "Default object list changed.");
                if (!defaults) Check(objects[0].GetProperty("tileLayerId").GetInt32() == 7 && objects[0].GetProperty("label").GetString() == "基地 İ"
                    && objects[0].GetProperty("pathTiles").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 2, 3 }), "Object fields changed.");
            }
            else Check(!root.TryGetProperty("authToken", out _), "Marker read acquired an unversioned body field.");
            if (!legacy) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(env.CurrentLanguage), "Language header changed.");
            return Json(write ? "{\"result\":{\"accepted\":true},\"acceptedCount\":1}" : "{\"userId\":\"用户 İ\",\"giftsEnabled\":false,\"pvpEnabled\":false,\"markers\":[{\"tileLayerId\":7}]}");
        }));
        var result = await Invoke(http, context, write, legacy, defaults, CancellationToken.None);
        Check(result.Success && calls == 1 && (write ? result.Accepted == true : result.Delivery?.PvpEnabled == false && result.Delivery.Markers[0].TileLayerId == 7), "Protocol/facade result changed.");
    }
    private static async Task Fault(bool write, string fault)
    {
        ClientSessionWork.Reset(); using var cancel = new CancellationTokenSource(); int calls = 0;
        if (fault == "cancel-before") cancel.Cancel();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++; if (fault == "cancel-response") cancel.Cancel(); if (fault == "generation") ClientSessionWork.Reset();
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") }
                : Json(fault == "malformed" ? "{" : fault == "reject" ? "{\"result\":{\"accepted\":false}}" : "{}"));
        }));
        var result = await Invoke(http, Context(), write, false, false, cancel.Token);
        Check(result.Success == (fault is "reject" or "missing-result") && result.Accepted != true && calls == (fault == "cancel-before" ? 0 : 1), "Fault result changed: " + fault);
    }
    private static async Task Missing(bool write, string missing)
    {
        ClientSessionWork.Reset(); var context = new ClashOfRimClientNetworkContext(missing == "server" ? "" : "http://localhost/base", missing == "user" ? "" : "user", missing == "colony" ? "" : "colony", null, null, null, null);
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Missing identity reached HTTP.")));
        Check((await Invoke(http, context, write, false, false, CancellationToken.None)).Error == "NotConfigured", "Missing identity not rejected.");
    }
    private static async Task<(bool Success, bool? Accepted, string? Error, ModWorldMapMarkerDeliveryDto? Delivery)> Invoke(HttpClient http,
        ClashOfRimClientNetworkContext context, bool write, bool legacy, bool defaults, CancellationToken token)
    {
        var protocol = new ClientWorldMarkerProtocol(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", null, null, null, null), new ClientProtocolTestPorts.Environment()), () => "not configured");
        var facade = legacy ? new ClashOfRimModNetworkClient(http, context) : null;
        if (write)
        {
            var values = defaults ? null! : new List<ModRuntimeWorldObjectMarkerDto> { new() { WorldObjectId = "object", Kind = "Caravan", Tile = 2, TileLayerId = 7, Label = "基地 İ", PathTiles = new() { 2, 3 } } };
            var result = legacy ? await facade!.SyncRuntimeWorldObjectsAsync(values, token) : await protocol.WriteAsync(context, values, token);
            return (result.Success, result.Response?.Result?.Accepted, result.ErrorCode, result.Response?.WorldMapMarkers);
        }
        var read = legacy ? await facade!.SyncWorldMapMarkersAsync(token) : await protocol.ReadAsync(context, token);
        return (read.Success, null, read.ErrorCode, read.Response);
    }
    private static ClashOfRimClientNetworkContext Context() => new("http://localhost/base", "用户 İ", "colony", "base", null, null, "auth");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
