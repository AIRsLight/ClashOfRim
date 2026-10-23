using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Transport;

// The caller owns HttpClient. Each request owns its content, response and linked cancellation.
internal sealed class ClientProtocolTransport
{
    internal const string LanguageHeader = "X-ClashOfRim-Language";
    private readonly HttpClient http;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly IClientProtocolEnvironment environment;

    internal ClientProtocolTransport(HttpClient http, ClashOfRimClientNetworkContext context, IClientProtocolEnvironment environment)
    { this.http = http; this.context = context; this.environment = environment; }

    internal ClientProtocolTransport ForContext(ClashOfRimClientNetworkContext owner) => new(http, owner, environment);

    internal Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> PostLoginAsync(string route, ModLoginRequestDto request,
        CancellationToken token, Action<ModLoginResponseDto>? captureReceipt) =>
        SendAsync(route, HttpMethod.Post, () => ClientProtocolJson.Serialize(request), ReadJson<ModLoginResponseDto>, ClientProtocolExchange.Json, token, captureReceipt: captureReceipt);

    internal Task<ClashOfRimClientNetworkResult<TResponse>> PostAsync<TRequest, TResponse>(string route, TRequest request,
        CancellationToken cancellationToken, bool bindToSession = true) =>
        SendAsync(route, HttpMethod.Post, () => ClientProtocolJson.Serialize(request), ReadJson<TResponse>, ClientProtocolExchange.Json, cancellationToken, bindToSession);

    internal Task<ClashOfRimClientNetworkResult<TResponse>> GetAsync<TResponse>(string route, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Get, null, ReadJson<TResponse>, ClientProtocolExchange.Json, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<byte[]>> PostForBytesAsync<TRequest>(string route, TRequest request, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Post, () => ClientProtocolJson.Serialize(request), ReadBytes, ClientProtocolExchange.Download, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<TResponse>> PostSnapshotMultipartAsync<TRequest, TResponse>(
        string route, TRequest request, byte[] payload, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Post, () => ClientProtocolJson.Serialize(request), ReadJson<TResponse>, ClientProtocolExchange.Multipart, cancellationToken, payload: payload);

    private async Task<ClashOfRimClientNetworkResult<TResponse>> SendAsync<TResponse>(string route, HttpMethod method,
        Func<string>? serializeRequest, Func<HttpContent, CancellationToken, Task<(TResponse Value, string? Json)>> read,
        ClientProtocolExchange exchange, CancellationToken cancellationToken, bool bindToSession = true, Action<TResponse>? captureReceipt = null, byte[]? payload = null)
    {
        using var sessionCancellation = bindToSession
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ClientSessionWork.Token) : null;
        CancellationToken token = sessionCancellation?.Token ?? cancellationToken;
        HttpRequestMessage? message = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var uri = new Uri(ClashOfRimServerUrlUtility.BuildHttpBaseUri(context.ServerBaseUrl), route.TrimStart('/'));
            message = new HttpRequestMessage(method, uri);
            string? requestJson = serializeRequest?.Invoke();
            message.Content = requestJson is null ? null : exchange == ClientProtocolExchange.Multipart
                ? MultipartContent(requestJson, payload) : JsonContent(requestJson);
            var observation = bindToSession ? ClientProtocolSessionObservation.Capture(environment, context, requestJson, exchange) : null;
            string? language = environment.CurrentLanguage;
            if (!string.IsNullOrWhiteSpace(language))
                message.Headers.Add(LanguageHeader, ServerLocalization.EncodeLanguageHeader(language));
            // JSON commands carry authentication in their DTO. Multipart and binary download also use the header.
            if (exchange != ClientProtocolExchange.Json && !string.IsNullOrWhiteSpace(context.AuthToken))
                message.Headers.TryAddWithoutValidation(MultipartSnapshotTransport.AuthenticationHeaderName, context.AuthToken);
            using HttpResponseMessage response = await http.SendAsync(message,
                exchange == ClientProtocolExchange.Download ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                token).ConfigureAwait(false);
            if (captureReceipt is null || !response.IsSuccessStatusCode) token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var rejection = ClientProtocolRejection.Read(body);
                observation?.Observe(rejection, allowHeaderRejection: true);
                bool incompatible = rejection?.Code == ProtocolErrorCode.IncompatibleProtocolVersion;
                string? hint = environment.HttpFailureHint(uri.AbsolutePath, (int)response.StatusCode, incompatible);
                string detail = incompatible && !string.IsNullOrWhiteSpace(rejection!.Message) ? rejection.Message! : body;
                return ClashOfRimClientNetworkResult<TResponse>.Failed(incompatible ? nameof(ProtocolErrorCode.IncompatibleProtocolVersion) : "HttpError",
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: " + (hint is null ? "" : hint + "\n") + detail,
                    incompatible ? rejection!.ServerProtocolVersion : null);
            }
            // Login uses ResponseContentRead: a returned response is already fully buffered.
            // Capture a decoded credential for cleanup before rejecting a canceled completion.
            // This never detaches the HTTP request itself from its caller/session cancellation.
            var decoded = await read(response.Content, captureReceipt is null ? token : CancellationToken.None).ConfigureAwait(false);
            TResponse parsed = decoded.Value;
            captureReceipt?.Invoke(parsed);
            token.ThrowIfCancellationRequested();
            observation?.Observe(decoded.Json, allowHeaderRejection: false);
            return ClashOfRimClientNetworkResult<TResponse>.Ok(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or SerializationException or IOException)
        { return ClashOfRimClientNetworkResult<TResponse>.Failed(ex.GetType().Name, ex.Message); }
        finally
        {
            if (exchange == ClientProtocolExchange.Multipart) DisposeMultipart(message);
            else message?.Dispose();
        }
    }

    private async Task<(T Value, string? Json)> ReadJson<T>(HttpContent content, CancellationToken token)
    {
        string body = await content.ReadAsStringAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return (ClientProtocolJson.Deserialize<T>(body, environment.DeserializationFailure), body);
    }

    private static async Task<(byte[] Value, string? Json)> ReadBytes(HttpContent content, CancellationToken token) =>
        (await content.ReadAsByteArrayAsync(token).ConfigureAwait(false), null);

    private static HttpContent JsonContent(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    private HttpContent MultipartContent(string json, byte[]? payload)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(JsonContent(json), "request");
            content.Add(new ByteArrayContent(payload ?? Array.Empty<byte>()), "payload", "snapshot.payload");
            return content;
        }
        catch { DisposeMultipart(content); throw; }
    }

    private void DisposeMultipart(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (NullReferenceException ex) { environment.MultipartDisposalFailed(ex.Message); }
    }
}
