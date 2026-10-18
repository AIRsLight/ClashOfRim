using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Protocol;
using Fixture = ClientSessionRecoveryTests.Fixture;

internal static class ClientHttpSessionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Shapes = { "json", "error", "header", "multipart", "download" };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string shape in Shapes)
                {
                    foreach (string text in new[] { "会话已过期", "session has expired", "Votre connexion est terminée.", "認証できません。", "İzin verilemiyor." })
                    { await Rejection(shape, text); count++; }
                    foreach (string change in new[] { "none", "user", "auth", "server", "colony", "login", "same-login", "work", "snapshot", "password", "steam", "game", "world" })
                    { await Ownership(shape, change); count++; }
                    foreach (string credential in new[] { "missing", "other", "current" })
                    { await Credentials(shape, credential); count++; }
                }
                foreach (var invalid in InvalidResults()) { await InvalidResult(invalid.Body, invalid.HttpError); count++; }
                foreach (bool detached in new[] { false, true })
                foreach (string source in new[] { "current", "other-session", "other-user", "other-colony" })
                { await Logout(detached, source); count++; }
                await PublicFacade(); await TemporaryContext(); await QueuedReplacement(); await UnauthenticatedAndBinary();
                await MutableRequest(); await DeferredInvalidation(); count += 6;
                foreach (string kind in new[] { "gift-header", "gift-result", "preview" }) { await HeaderOnly(kind); count++; }
            }
            Console.WriteLine($"PASS: HTTP session rejection and request ownership ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static string Body(string shape, string message = "異なる言語", ProtocolErrorCode code = ProtocolErrorCode.SessionUnavailable)
    {
        var result = ProtocolResponse.Reject(code, message);
        return shape == "header" ? JsonSerializer.Serialize(result, Web) : JsonSerializer.Serialize(new { result }, Web);
    }
    private static HttpResponseMessage Response(string shape, string? body = null) => new(
        shape is "json" or "multipart" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
        { Content = new StringContent(body ?? Body(shape), Encoding.UTF8, "application/json") };
    private static ModListChatMessagesRequestDto Request(string? auth = "auth") => new()
        { UserId = "alice", ColonyId = "colony", AuthToken = auth, Limit = 20 };
    private static async Task Send(ClientProtocolTransport transport, string shape, ModListChatMessagesRequestDto? request = null, bool canceled = false)
    {
        request ??= Request();
        bool success; string? error;
        if (shape == "download")
        {
            var result = await transport.PostForBytesAsync("snapshots/latest/payload", request, default);
            success = result.Success; error = result.ErrorCode;
        }
        else
        {
            var result = shape is "header" or "multipart"
                ? await transport.PostSnapshotMultipartAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>("test", request, new byte[] { 1, 2 }, default)
                : await transport.PostAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>("test", request, default);
            success = result.Success; error = result.ErrorCode;
            if (result.Success) Check(result.Response!.Result is { Accepted: false } rejection
                && rejection.ErrorCode == (int)ProtocolErrorCode.SessionUnavailable, "Protocol rejection was lost.");
        }
        if (canceled) Check(!success && error is "OperationCanceledException" or "TaskCanceledException", "Canceled request accepted a late response.");
        else Check(success == (shape is "json" or "multipart") && (success || error == "HttpError"), "Structured rejection changed transport result: " + shape);
    }
    private static async Task Rejection(string shape, string message)
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response(shape, Body(shape, message)))));
        await Send(new(http, f.Ports.ReadContext(), environment), shape);
        Check(f.Session.IsUnavailable && f.Ports.Queued == 1 && environment.Captures == 1, "Localized structured rejection was ignored: " + shape);
        (await f.Ports.Next())();
        Check(f.Dialog is not null && f.Ports.Auth == "", "Structured expiry did not use the recovery module.");
    }
    private static async Task Ownership(string shape, string change)
    {
        var f = new Fixture(); var environment = new Environment(f);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => reply.Task));
        var transport = new ClientProtocolTransport(http, f.Ports.ReadContext(), environment);
        // Send starts synchronously through capture before awaiting the handler.
        var work = Send(transport, shape, canceled: change == "work");
        Check(environment.Captures == 1 && !work.IsCompleted, "Source was not captured before the request.");
        if (change == "same-login") f.Session.PublishLogin(new() { Result = new() { Accepted = true }, AuthToken = "auth", SessionId = "session" });
        else f.Change(change);
        reply.SetResult(Response(shape));
        await work;
        bool eligible = change is "none" or "snapshot" or "password" or "steam" or "game" or "world";
        Check(f.Session.IsUnavailable == eligible && f.Ports.Queued == (eligible ? 1 : 0), "Old reply crossed login ownership: " + shape + "/" + change);
    }
    private static async Task Credentials(string shape, string credential)
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response(shape))));
        await Send(new(http, f.Ports.ReadContext(), environment), shape, Request(credential switch { "missing" => null, "other" => "candidate", _ => "auth" }));
        bool eligible = shape == "header" || credential == "current";
        Check(f.Session.IsUnavailable == eligible && f.Ports.Queued == (eligible ? 1 : 0), "Failure was attributed to the wrong transmitted credential.");
    }
    private static IEnumerable<(string Body, bool HttpError)> InvalidResults()
    {
        foreach (string body in new[] {
            "{\"result\":{\"accepted\":true,\"errorCode\":12}}", "{\"result\":{\"errorCode\":12}}",
            "{\"result\":{\"accepted\":false}}", "{\"result\":{\"accepted\":false,\"errorCode\":1}}",
            "{\"result\":{\"accepted\":false,\"errorCode\":8,\"message\":\"session has expired 会话已过期\"}}",
            "{\"Result\":{\"Accepted\":false,\"ErrorCode\":12}}", "{\"extra\":{\"result\":{\"accepted\":false,\"errorCode\":12}}}",
            "{\"accepted\":false,\"errorCode\":12}", "{\"result\":null}", "{}" })
            yield return (body, false);
        foreach (string body in new[] {
            "session has expired", "会话已过期", "<html>Unauthorized</html>", "null", "[]", "{broken",
            "{\"result\":{\"accepted\":\"false\",\"errorCode\":12}}", "{\"result\":{\"accepted\":false,\"errorCode\":\"12\"}}",
            "{\"result\":{\"accepted\":false,\"errorCode\":12.5}}", "{\"result\":{\"accepted\":false,\"errorCode\":2147483648}}",
            "{\"result\":{\"accepted\":true,\"accepted\":false,\"errorCode\":12}}",
            "{\"result\":{\"accepted\":false,\"errorCode\":1,\"errorCode\":12}}",
            "{\"result\":{\"accepted\":true},\"result\":{\"accepted\":false,\"errorCode\":12},\"accepted\":false,\"errorCode\":12}" })
            yield return (body, true);
    }
    private static async Task InvalidResult(string body, bool error)
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(new HttpResponseMessage(error ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(body) })));
        await new ClientProtocolTransport(http, f.Ports.ReadContext(), environment)
            .PostSnapshotMultipartAsync<ModListChatMessagesRequestDto, ModListChatMessagesResponseDto>("test", Request(), Array.Empty<byte>(), default);
        Check(!f.Session.IsUnavailable && f.Ports.Queued == 0, "Non-rejection or ambiguous JSON invalidated a session: " + body);
    }
    private static async Task Logout(bool detached, string source)
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response("json"))));
        await new ClientProtocolTransport(http, f.Ports.ReadContext(), environment).PostAsync<ModLogoutRequestDto, ModLogoutResponseDto>("logout", new()
        { UserId = source == "other-user" ? "bob" : "alice", ColonyId = source == "other-colony" ? "other" : "colony",
            SessionId = source == "other-session" ? "candidate-session" : "session" }, default, bindToSession: !detached);
        Check(f.Session.IsUnavailable == (!detached && source == "current") && (!detached || environment.Captures == 0), "Logout rejection affected a foreign or detached session.");
    }
    private static async Task PublicFacade()
    {
        var previous = ClashOfRimModNetworkClient.CaptureSessionUnavailable;
        try
        {
            var f = new Fixture(); int replacements = 0;
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ClashOfRimModNetworkClient.CaptureSessionUnavailable = f.Recovery.CaptureUnavailable;
            using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => reply.Task));
            var work = new ClashOfRimModNetworkClient(http, f.Ports.ReadContext()).ListChatMessagesAsync(0);
            ClashOfRimModNetworkClient.CaptureSessionUnavailable = (_, _) => { replacements++; return () => replacements++; };
            reply.SetResult(Response("json")); await work;
            Check(f.Session.IsUnavailable && replacements == 0, "Public facade looked up the recovery target after its request completed.");
        }
        finally { ClashOfRimModNetworkClient.CaptureSessionUnavailable = previous; }
    }
    private static async Task TemporaryContext()
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response("multipart"))));
        var current = new ClientProtocolTransport(http, f.Ports.ReadContext(), environment);
        var temporary = new ClashOfRimClientNetworkContext(f.Ports.Server, "alice", "colony", "base", null, null, "candidate");
        await Send(current.ForContext(temporary), "multipart", Request("candidate"));
        Check(!f.Session.IsUnavailable && f.Ports.Auth == "auth" && f.Ports.Queued == 0, "Temporary setup/login credential invalidated the live identity.");
    }
    private static async Task QueuedReplacement()
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response("json"))));
        await Send(new(http, f.Ports.ReadContext(), environment), "json");
        var old = await f.Ports.Next(); f.Change("login"); old();
        Check(f.Dialog is null && f.Ports.Auth == "new-token" && !f.Session.IsUnavailable, "Queued HTTP expiry cleared the next login.");
    }
    private static async Task UnauthenticatedAndBinary()
    {
        var f = new Fixture(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response("json"))));
        var transport = new ClientProtocolTransport(http, f.Ports.ReadContext(), environment);
        await transport.GetAsync<ModListChatMessagesResponseDto>("public", default);
        await transport.PostLoginAsync("login", new ModLoginRequestDto { UserId = "alice", ColonyId = "colony" }, default, null);
        var binary = await transport.PostForBytesAsync("binary", Request(), default);
        Check(binary.Success && Encoding.UTF8.GetString(binary.Response!) == Body("json") && !f.Session.IsUnavailable && f.Ports.Queued == 0,
            "Unauthenticated request or successful binary content caused expiry.");
    }
    private static async Task MutableRequest()
    {
        var f = new Fixture(); var environment = new Environment(f); var request = Request();
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler(async (message, _) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("authToken").GetString() == "auth", "Request did not serialize its original credential.");
            return await reply.Task;
        }));
        var work = Send(new(http, f.Ports.ReadContext(), environment), "json", request);
        request.AuthToken = "candidate"; request.UserId = "bob";
        reply.SetResult(Response("json")); await work;
        Check(f.Session.IsUnavailable && f.Ports.Queued == 1, "Response attribution reread a mutable request DTO.");
    }
    private static async Task DeferredInvalidation()
    {
        var f = new Fixture(); var context = f.Ports.ReadContext(); var environment = new Environment(f);
        using var hold = f.Session.HoldInvalidation(f.Session.CaptureOwnership(), context)!;
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((_, _) => Task.FromResult(Response("json"))));
        await Send(new(http, context, environment), "json");
        Check(f.Session.IsUnavailable && f.Ports.Auth == "auth" && f.Ports.Queued == 0, "HTTP expiry bypassed the terminal operation hold.");
        hold.Dispose(); (await f.Ports.Next())();
        Check(f.Dialog is not null && f.Ports.Auth == "", "Held HTTP expiry was lost when the operation finished.");
    }
    private static async Task HeaderOnly(string kind)
    {
        var f = new Fixture(); var context = f.Ports.ReadContext(); var environment = new Environment(f);
        using var http = new HttpClient(new ClientProtocolTestPorts.Handler((message, _) =>
        {
            Check(message.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == "auth", "Header-only request omitted its credential.");
            return Task.FromResult(Response(kind == "gift-result" ? "multipart" : "header"));
        }));
        var transport = new ClientProtocolTransport(http, context, environment);
        if (kind == "preview")
        {
            var response = await transport.PostForBytesAsync(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackagePreview).Route,
                new ModGetPawnPackagePreviewRequestDto { Requester = new() { UserId = "alice", ColonyId = "colony" }, PawnPackageId = "pawn" }, default);
            Check(!response.Success && response.ErrorCode == "HttpError", "Preview HTTP rejection changed.");
        }
        else
        {
            var protocol = new ClientGiftProtocol(transport, () => "unconfigured");
            var response = await protocol.CreateGiftWithSnapshotAsync(context, "gift", "bob", "home", null,
                Array.Empty<ModThingReferenceDto>(), null, null, null,
                new() { SnapshotId = "after", PreviousSnapshotId = "base", OwnerId = "alice", ColonyId = "colony" }, new byte[] { 1 });
            Check(response.Success == (kind == "gift-result"), "Gift HTTP rejection changed.");
        }
        Check(f.Session.IsUnavailable && f.Ports.Queued == 1 && environment.Captures == 1, "Header-only protocol lost its session owner: " + kind);
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        private readonly Fixture f;
        internal int Captures;
        internal Environment(Fixture f) => this.f = f;
        public string? CurrentLanguage => "日本語";
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(ClashOfRimClientNetworkContext context, string? sessionId)
        { Captures++; return f.Recovery.CaptureUnavailable(context, sessionId); }
        public void MultipartDisposalFailed(string message) => throw new InvalidOperationException(message);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
