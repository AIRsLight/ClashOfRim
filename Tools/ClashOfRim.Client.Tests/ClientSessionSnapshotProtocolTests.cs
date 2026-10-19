using static ClientProtocolTestPorts;
using Environment = ClientProtocolTestPorts.Environment;
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
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientSessionSnapshotProtocolTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                foreach (bool facade in new[] { false, true })
                foreach (bool defaults in new[] { false, true })
                {
                    await Presence(facade, defaults);
                    foreach (var (kind, operation, expected) in new (string?, string?, string)[]
                    {
                        (null, null, "ManualUpload"), ("", "EndgameAchievement", "EndgameAchievement"),
                        (" ", "ColonyRelocation", "ColonyRelocation"), (null, "endgameachievement", "ManualUpload"),
                        (null, " EndgameAchievement ", "ManualUpload"), (null, "Custom İ", "ManualUpload"),
                        ("AutoSave", "EndgameAchievement", "AutoSave"), ("Custom İ", "ColonyRelocation", "Custom İ"),
                        (" Explicit ", null, " Explicit ")
                    }) await Snapshot(facade, defaults, kind, operation, expected);
                }
                foreach (bool upload in new[] { false, true })
                {
                    foreach (string missing in new[] { "server", "user", "colony" }) await Missing(upload, missing);
                    foreach (string fault in new[] { "http", "rejected", "missing-result", "malformed", "cancel-before", "cancel-response", "generation" }) await Failure(upload, fault);
                }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ClashOfRimClientNetworkContext Context(string missing = "") => new(missing == "server" ? "" : "http://owner/base",
        missing == "user" ? "" : "玩家 İ", missing == "colony" ? "" : "colony", "base-snapshot", null, null, "captured-token");
    private static ClientProtocolTransport Transport(HttpClient http, Environment env) => new(http,
        new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong"), env);
    private static void Envelope(HttpRequestMessage request, ProtocolMessageKind kind, bool multipart, bool facade, Environment env)
    {
        Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri(new Uri("http://owner/base/"), ProtocolContractManifest.Find(kind).Route.TrimStart('/')), "Protocol borrowed the transport's original context or changed route.");
        Check(request.Headers.Contains(MultipartSnapshotTransport.AuthenticationHeaderName) == multipart, "Authentication envelope changed.");
        if (multipart) Check(request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == "captured-token", "Multipart header borrowed another credential.");
        if (!facade) Check(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(env.CurrentLanguage), "Language header changed.");
    }
    private static async Task Presence(bool facade, bool defaults)
    {
        ClientSessionWork.Reset(); var context = Context(); var env = new Environment(); int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Envelope(request, ProtocolMessageKind.MaintainPresence, false, facade, env);
            Check(request.Content!.Headers.ContentType!.MediaType == "application/json", "Presence stopped using JSON.");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)); var root = body.RootElement;
            Check(root.GetProperty("userId").GetString() == "玩家 İ" && root.GetProperty("colonyId").GetString() == "colony"
                && root.GetProperty("currentSnapshotId").GetString() == "base-snapshot" && root.GetProperty("sessionId").GetString() == (defaults ? null : "session 文化"), "Presence fields/defaults changed.");
            return Json("{\"result\":{\"accepted\":true},\"userId\":\"玩家 İ\",\"online\":true}");
        }));
        string? session = defaults ? null : "session 文化";
        var result = facade ? await new ClashOfRimModNetworkClient(http, context).MaintainPresenceAsync(session)
            : await new ClientSessionProtocol(Transport(http, env), env, env).MaintainPresenceAsync(context, session);
        Check(result.Success && result.Response!.Online && result.Response.UserId == context.UserId && calls == 1, "Presence response or facade forwarding changed.");
    }
    private static async Task Snapshot(bool facade, bool defaults, string? kind, string? operation, string expected)
    {
        ClientSessionWork.Reset(); var context = Context(); var env = new Environment(); int calls = 0;
        byte[]? payload = defaults ? null : new byte[] { 0, 1, 255, 13, 10 };
        var metadata = new ModSnapshotPackageMetadataDto
        {
            SnapshotId = "prepared", OwnerId = "package-owner", ColonyId = "package-colony", PreviousSnapshotId = "previous",
            LineageToken = "lineage", NextLineageToken = "next", SnapshotUploadKind = kind, GameTicks = long.MaxValue - 1,
            DefenderThreatPoints = 123.25f, OriginalSaveBytes = 12345, PayloadBytes = 5, PayloadSha256 = "payload-hash",
            OriginalSha256 = "save-hash", PayloadEncoding = "GzipRws", RimWorldVersion = "1.7", PackageVersion = "v1"
        };
        var awards = defaults ? null : new[] { new ModSnapshotAchievementCandidateDto { AchievementId = "award", EventKey = "event İ", Value = long.MaxValue, MetadataJson = "{\"name\":\"文化\"}" } };
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++; Envelope(request, ProtocolMessageKind.UploadSnapshot, true, facade, env);
            var form = (MultipartFormDataContent)request.Content!;
            Check(form.Count() == 2, "Upload duplicated multipart fields.");
            var binary = form.Single(c => c.Headers.ContentDisposition!.Name!.Trim('"') == "payload");
            Check((await binary.ReadAsByteArrayAsync(token)).SequenceEqual(payload ?? Array.Empty<byte>())
                && binary.Headers.ContentDisposition!.FileName!.Trim('"') == "snapshot.payload", "Snapshot bytes or file name changed.");
            string json = await form.Single(c => c.Headers.ContentDisposition!.Name!.Trim('"') == "request").ReadAsStringAsync(token);
            using var body = JsonDocument.Parse(json); var root = body.RootElement; var package = root.GetProperty("package");
            Check(root.GetProperty("idempotencyKey").GetString() == "key İ" && root.GetProperty("userId").GetString() == context.UserId
                && root.GetProperty("colonyId").GetString() == context.ColonyId && root.GetProperty("authToken").GetString() == context.AuthToken
                && root.GetProperty("snapshotId").GetString() == "prepared" && root.GetProperty("confirmationOperation").GetString() == operation, "Upload identity or operation changed.");
            Check(package.GetProperty("snapshotUploadKind").GetString() == expected && package.GetProperty("ownerId").GetString() == "package-owner"
                && package.GetProperty("colonyId").GetString() == "package-colony" && package.GetProperty("previousSnapshotId").GetString() == "previous"
                && package.GetProperty("lineageToken").GetString() == "lineage" && package.GetProperty("nextLineageToken").GetString() == "next"
                && package.GetProperty("gameTicks").GetInt64() == long.MaxValue - 1 && package.GetProperty("defenderThreatPoints").GetSingle() == 123.25f
                && package.GetProperty("originalSaveBytes").GetInt64() == 12345 && package.GetProperty("payloadBytes").GetInt64() == 5
                && package.GetProperty("payloadSha256").GetString() == "payload-hash" && package.GetProperty("originalSha256").GetString() == "save-hash"
                && package.GetProperty("payloadEncoding").GetString() == "GzipRws" && package.GetProperty("rimWorldVersion").GetString() == "1.7"
                && package.GetProperty("packageVersion").GetString() == "v1", "Package kind, provenance or numeric metadata changed.");
            var candidates = root.GetProperty("achievementCandidates"); Check(candidates.GetArrayLength() == (defaults ? 0 : 1), "Default candidate list changed.");
            if (!defaults) Check(candidates[0].GetProperty("eventKey").GetString() == "event İ" && candidates[0].GetProperty("value").GetInt64() == long.MaxValue
                && candidates[0].GetProperty("metadataJson").GetString() == "{\"name\":\"文化\"}", "Achievement metadata changed.");
            return Json("{\"result\":{\"accepted\":true},\"acceptedSnapshotId\":\"accepted\",\"nextLineageToken\":\"receipt\"}");
        }));
        var result = facade ? await new ClashOfRimModNetworkClient(http, context).UploadSnapshotAsync("key İ", metadata, payload!, operation, awards)
            : await new ClientSnapshotUploadProtocol(Transport(http, env), () => "not configured").UploadAsync(context, "key İ", metadata, payload!, operation, awards);
        Check(result.Success && result.Response!.AcceptedSnapshotId == "accepted" && result.Response.NextLineageToken == "receipt"
            && metadata.SnapshotUploadKind == expected && calls == 1, "Upload response, input kind annotation or facade forwarding changed.");
    }
    private static async Task Missing(bool upload, string missing)
    {
        ClientSessionWork.Reset(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var result = await Invoke(http, Context(missing), upload, CancellationToken.None);
        Check(!result.Success && result.Code == "NotConfigured" && calls == 0, "Unconfigured protocol sent a request: " + missing);
    }
    private static async Task Failure(bool upload, string fault)
    {
        ClientSessionWork.Reset(); using var cancellation = new CancellationTokenSource(); int calls = 0;
        if (fault == "cancel-before") cancellation.Cancel();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (fault == "cancel-response") cancellation.Cancel();
            if (fault == "generation") ClientSessionWork.Reset();
            return Task.FromResult(fault switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") },
                "rejected" => Json("{\"result\":{\"accepted\":false,\"message\":\"business rejection\"}}"),
                "missing-result" => Json("{}"), "malformed" => Json("{broken"), _ => Json("{\"result\":{\"accepted\":true}}")
            });
        }));
        var result = await Invoke(http, Context(), upload, cancellation.Token);
        Check(result.Success == (fault is "rejected" or "missing-result") && calls == (fault == "cancel-before" ? 0 : 1), "Failure/cancellation changed: " + fault);
        if (fault == "http") Check(result.Code == "HttpError", "HTTP error lost its category.");
        if (fault is "rejected" or "missing-result") Check(result.Accepted == (fault == "rejected" ? false : null), "Protocol promoted missing/rejected business result.");
    }
    private static async Task<(bool Success, string? Code, bool? Accepted)> Invoke(HttpClient http, ClashOfRimClientNetworkContext context, bool upload, CancellationToken token)
    {
        var env = new Environment(); var transport = Transport(http, env);
        if (upload)
        {
            var result = await new ClientSnapshotUploadProtocol(transport, () => "not configured").UploadAsync(context, "key", new() { SnapshotId = "prepared" }, Array.Empty<byte>(), cancellationToken: token);
            return (result.Success, result.ErrorCode, result.Response?.Result?.Accepted);
        }
        var presence = await new ClientSessionProtocol(transport, env, env).MaintainPresenceAsync(context, "session", token);
        return (presence.Success, presence.ErrorCode, presence.Response?.Result?.Accepted);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
