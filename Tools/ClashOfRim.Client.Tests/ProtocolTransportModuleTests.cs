using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ProtocolTransportModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await DownloadBodyCancellation();
            await RequestsAndOwnership();
            await ErrorsAndExpiry();
            await ProtocolRejections();
            await SessionCancellationAndDetachedCleanup();
            await MultipartDisposal();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static readonly ClashOfRimClientNetworkContext Context = new("http://localhost/base", "alice", "colony", "snapshot", null, null, "owner-token");
    private static readonly byte[] Payload = { 0, 127, 128, 255 };
    private static readonly Request RequestData = new() { Text = "中文 à : ", Values = new() { ["count"] = 3 } };
    private static Task<ClashOfRimClientNetworkResult<Response>> JsonExchange(ClientProtocolTransport transport, string kind, CancellationToken token = default) => kind switch
    {
        "get" => transport.GetAsync<Response>("/test?value=1", token),
        "post" => transport.PostAsync<Request, Response>("/test?value=1", RequestData, token),
        _ => transport.PostSnapshotMultipartAsync<Request, Response>("/test?value=1", RequestData, Payload, token)
    };

    private static async Task RequestsAndOwnership()
    {
        foreach (string kind in new[] { "get", "post", "multipart", "download" })
        {
            var environment = new Environment { Language = "简体中文" };
            HttpContent? requestContent = null;
            TrackingContent? responseContent = null;
            int requests = 0;
            using var handler = new Handler(async (request, token) =>
            {
                requests++;
                Require(request.RequestUri!.AbsoluteUri == "http://localhost/base/test?value=1"
                    && request.Method == (kind == "get" ? HttpMethod.Get : HttpMethod.Post), "Method or base-path resolution changed.");
                Require(request.Headers.GetValues(ClientProtocolTransport.LanguageHeader).Single() == ServerLocalization.EncodeLanguageHeader(environment.Language!),
                    "Language header lost its wire encoding.");
                bool header = request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var auth);
                Require(header == (kind is "multipart" or "download") && (!header || auth!.Single() == "owner-token"), "Authentication header policy changed.");
                requestContent = request.Content;
                if (kind == "get") Require(request.Content is null, "GET has an unexpected body.");
                else
                {
                    HttpContent metadata = request.Content!;
                    if (kind == "multipart")
                    {
                        var parts = ((MultipartFormDataContent)request.Content!).ToArray();
                        Require(parts.Length == 2 && parts[0].Headers.ContentDisposition!.Name!.Trim('"') == "request"
                            && parts[1].Headers.ContentDisposition!.Name!.Trim('"') == "payload"
                            && parts[1].Headers.ContentDisposition!.FileName!.Trim('"') == "snapshot.payload"
                            && (await parts[1].ReadAsByteArrayAsync(token)).SequenceEqual(Payload), "Multipart payload or names changed.");
                        metadata = parts[0];
                    }
                    Require(metadata.Headers.ContentType!.MediaType == "application/json", "JSON content type changed.");
                    using var json = JsonDocument.Parse(await metadata.ReadAsStringAsync(token));
                    Require(json.RootElement.GetProperty("text").GetString() == RequestData.Text
                        && json.RootElement.GetProperty("values").GetProperty("count").GetInt32() == 3
                        && !json.RootElement.TryGetProperty("Ignored", out _), "DataContract or dictionary encoding changed.");
                }
                responseContent = new TrackingContent(kind == "download" ? Payload : Encoding.UTF8.GetBytes("{\"result\":{\"accepted\":true},\"text\":\"réponse 中文\"}"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent };
            });
            using var http = new HttpClient(handler);
            var transport = new ClientProtocolTransport(http, Context, environment);
            // Reuse the caller's HttpClient after the first request completed and disposed its owned objects.
            for (int repeat = 0; repeat < 2; repeat++)
            {
                if (kind == "download")
                {
                    var result = await transport.PostForBytesAsync("/test?value=1", RequestData, default);
                    Require(result.Success && result.Response!.SequenceEqual(Payload), "Binary body was decoded as JSON/text.");
                }
                else
                {
                    var result = await JsonExchange(transport, kind);
                    Require(result.Success && result.Response!.Result!.Accepted && result.Response.Text == "réponse 中文", "Typed response changed.");
                }
                Require(responseContent!.Disposals == 1, "Response content was not disposed exactly once.");
                if (requestContent is not null)
                {
                    try { await requestContent.ReadAsByteArrayAsync(); throw new Exception("Request content was not disposed."); }
                    catch (ObjectDisposedException) { }
                }
            }
            Require(requests == 2 && environment.Expired.Count == 0, "Transport disposed its caller or emitted spurious expiry.");
        }
    }

    private static async Task ErrorsAndExpiry()
    {
        foreach (string kind in new[] { "get", "post", "multipart", "download" })
        foreach (string message in new[] { "session has expired", "会话已过期", "ordinary rejection" })
        {
            var environment = new Environment();
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { ReasonPhrase = "Rejected", Content = new StringContent(message) }));
            using var http = new HttpClient(handler);
            var transport = new ClientProtocolTransport(http, Context, environment);
            if (kind == "download")
            {
                var result = await transport.PostForBytesAsync("/test", RequestData, default);
                Require(!result.Success && result.ErrorCode == "HttpError" && result.Message == "400 Rejected: " + message, "Download HTTP rejection changed.");
            }
            else
            {
                var result = await JsonExchange(transport, kind);
                Require(!result.Success && result.ErrorCode == "HttpError" && result.Message == "400 Rejected: " + message, "JSON HTTP rejection changed.");
            }
            Require(environment.Expired.Count == 0, "Human text alone invalidated a session.");
        }
        foreach (string kind in new[] { "get", "post", "multipart" })
        foreach (string body in new[] { "null", "{broken", "{\"result\":{\"accepted\":false,\"message\":\"session has expired\"}}", "{\"result\":{\"accepted\":true,\"message\":\"session has expired\"}}" })
        {
            var environment = new Environment();
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            using var http = new HttpClient(handler);
            var result = await JsonExchange(new(http, Context, environment), kind);
            if (body == "null") Require(!result.Success && result.ErrorCode == "InvalidOperationException" && result.Message == "decode:Response", "Null response lost presentation mapping.");
            else if (body == "{broken") Require(!result.Success && result.ErrorCode == "SerializationException", "Invalid JSON escaped error mapping.");
            else Require(result.Success && environment.Expired.Count == 0, "Protocol rejection or successful expiry text was misclassified.");
        }
        foreach (Exception failure in new Exception[] { new HttpRequestException("offline"), new IOException("broken pipe"), new InvalidOperationException("invalid") })
        {
            using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure));
            using var http = new HttpClient(handler);
            var result = await JsonExchange(new(http, Context, new Environment()), "post");
            Require(!result.Success && result.ErrorCode == failure.GetType().Name && result.Message == failure.Message, "Transport exception mapping changed.");
        }
    }

    private static async Task ProtocolRejections()
    {
        foreach (string kind in new[] { "get", "post", "multipart", "download" })
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable })
        foreach (bool envelope in new[] { false, true })
        {
            const string detail = "Client protocol old; server protocol expected. Update the client.";
            string rejection = JsonSerializer.Serialize(new { accepted = false, errorCode = (int)ProtocolErrorCode.IncompatibleProtocolVersion, message = detail });
            string body = envelope ? "{\"serverProtocolVersion\":\"server-wire\",\"result\":" + rejection + "}"
                : rejection.Insert(1, "\"protocolVersion\":\"server-wire\",");
            var environment = new Environment();
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
            using var http = new HttpClient(handler);
            var transport = new ClientProtocolTransport(http, Context, environment);
            var result = kind == "download" ? await transport.PostForBytesAsync("/test", RequestData, default) : null;
            var json = kind == "download" ? null : await JsonExchange(transport, kind);
            string? code = result?.ErrorCode ?? json?.ErrorCode, message = result?.Message ?? json?.Message;
            Require(code == nameof(ProtocolErrorCode.IncompatibleProtocolVersion) && message!.Contains(detail)
                && (result?.ServerProtocolVersion ?? json?.ServerProtocolVersion) == "server-wire"
                && !ClientNetworkFailurePolicy.CanRetry(code, message) && environment.Expired.Count == 0,
                "HTTP protocol rejection lost its reason, became retryable or expired a valid session.");
        }
        foreach (string body in new[] { "<html>503 protocol mismatch</html>", "{", "null", "{}",
            "{\"accepted\":true,\"errorCode\":2}", "{\"accepted\":false,\"errorCode\":\"2\"}",
            "{\"accepted\":false,\"errorCode\":2,\"errorCode\":0}", "{\"accepted\":false,\"accepted\":true,\"errorCode\":2}" })
        {
            var environment = new Environment();
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body) }));
            using var http = new HttpClient(handler);
            var result = await JsonExchange(new(http, Context, environment), "post");
            Require(result.ErrorCode == "HttpError" && ClientNetworkFailurePolicy.CanRetry(result.ErrorCode, result.Message)
                && environment.Expired.Count == 0, "Ambiguous 503 was classified as a protocol/session failure.");
        }
    }

    private static async Task SessionCancellationAndDetachedCleanup()
    {
        foreach (bool detached in new[] { false, true })
        {
            ClientSessionWork.Reset();
            var environment = new Environment();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default;
            using var handler = new Handler((_, token) => { observed = token; started.TrySetResult(); return response.Task; });
            using var http = new HttpClient(handler);
            var transport = new ClientProtocolTransport(http, Context, environment);
            var task = transport.PostAsync<Request, Response>("/cleanup", RequestData, default, bindToSession: !detached);
            await started.Task;
            ClientSessionWork.Reset();
            Require(observed.IsCancellationRequested != detached, "Detached cleanup inherited session cancellation, or an owned request lost it.");
            response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"result\":{\"accepted\":true}}") });
            var result = await task;
            Require(result.Success == detached && environment.Expired.Count == 0, "Late response escaped request ownership.");
        }
    }

    private static async Task MultipartDisposal()
    {
        var environment = new Environment();
        using var handler = new Handler((request, _) =>
        {
            request.Content!.Dispose();
            request.Content = new DisposalFailureContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true}}") });
        });
        using var http = new HttpClient(handler);
        var result = await JsonExchange(new(http, Context, environment), "multipart");
        Require(result.Success && environment.DisposalFailures == 1, "Legacy multipart disposal failure erased a received response.");
    }

    [DataContract]
    private sealed class Request
    {
        [DataMember(Name = "text")] public string Text { get; set; } = "";
        [DataMember(Name = "values")] public Dictionary<string, int> Values { get; set; } = new();
        public string Ignored => "not a wire field";
    }
    [DataContract]
    private sealed class Response
    {
        [DataMember(Name = "result")] public ModProtocolResponseDto? Result { get; set; }
        [DataMember(Name = "text")] public string? Text { get; set; }
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        internal string? Language;
        internal readonly List<string?> Expired = new();
        internal int DisposalFailures;
        public string? CurrentLanguage => Language;
        public string DeserializationFailure(string responseType) => "decode:" + responseType;
        public Action? CaptureSessionUnavailable(ClashOfRimClientNetworkContext context, string? sessionId) => () => Expired.Add(context.AuthToken);
        public void MultipartDisposalFailed(string message) => DisposalFailures++;
    }
    private sealed class TrackingContent : ByteArrayContent
    {
        internal int Disposals;
        internal TrackingContent(byte[] bytes) : base(bytes) { }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class DisposalFailureContent : ByteArrayContent
    {
        internal DisposalFailureContent() : base(Array.Empty<byte>()) { }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); throw new NullReferenceException("fixture multipart disposal"); }
    }

    private static async Task DownloadBodyCancellation()
    {
        foreach (bool endSession in new[] { false, true })
        foreach (HttpStatusCode status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            ClientSessionWork.Reset();
            using var content = new WaitingContent();
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));
            using var http = new HttpClient(handler);
            using var cancel = new CancellationTokenSource();
            var client = new ClashOfRimModNetworkClient(http, new("http://localhost", "alice", "colony", "snapshot", null, null, "token"));
            var download = client.DownloadLatestSnapshotPayloadAsync("snapshot", cancel.Token);
            try
            {
                await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (endSession) ClientSessionWork.Reset(); else cancel.Cancel();
                Require(await Task.WhenAny(download, Task.Delay(1000)) == download,
                    "Snapshot payload body keeps waiting after " + (endSession ? "session exit" : "caller cancellation") + ".");
                var result = await download;
                Require(!result.Success && result.ErrorCode!.EndsWith("CanceledException") && content.ReceivedToken.CanBeCanceled,
                    "Payload or error body did not honor its request's cancellation.");
            }
            finally { content.Release.TrySetResult(); await download; }
        }
    }

    private sealed class WaitingContent : HttpContent
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReceivedToken;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
