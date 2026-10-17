using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
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
    private enum Exchange { Json, Download, Multipart }
    private readonly HttpClient http;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly IClientProtocolEnvironment environment;

    internal ClientProtocolTransport(HttpClient http, ClashOfRimClientNetworkContext context, IClientProtocolEnvironment environment)
    { this.http = http; this.context = context; this.environment = environment; }

    internal Task<ClashOfRimClientNetworkResult<TResponse>> PostAsync<TRequest, TResponse>(string route, TRequest request,
        CancellationToken cancellationToken, bool bindToSession = true) =>
        SendAsync(route, HttpMethod.Post, () => JsonContent(request), ReadJson<TResponse>, Exchange.Json, cancellationToken, bindToSession);

    internal Task<ClashOfRimClientNetworkResult<TResponse>> GetAsync<TResponse>(string route, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Get, null, ReadJson<TResponse>, Exchange.Json, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<byte[]>> PostForBytesAsync<TRequest>(string route, TRequest request, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Post, () => JsonContent(request), (body, token) => body.ReadAsByteArrayAsync(token), Exchange.Download, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<TResponse>> PostSnapshotMultipartAsync<TRequest, TResponse>(
        string route, TRequest request, byte[] payload, CancellationToken cancellationToken) =>
        SendAsync(route, HttpMethod.Post, () => MultipartContent(request, payload), ReadJson<TResponse>, Exchange.Multipart, cancellationToken);

    private async Task<ClashOfRimClientNetworkResult<TResponse>> SendAsync<TResponse>(string route, HttpMethod method,
        Func<HttpContent>? createContent, Func<HttpContent, CancellationToken, Task<TResponse>> read,
        Exchange exchange, CancellationToken cancellationToken, bool bindToSession = true)
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
            message.Content = createContent?.Invoke();
            string? language = environment.CurrentLanguage;
            if (!string.IsNullOrWhiteSpace(language))
                message.Headers.Add(LanguageHeader, ServerLocalization.EncodeLanguageHeader(language));
            // JSON commands carry authentication in their DTO. Multipart and binary download also use the header.
            if (exchange != Exchange.Json && !string.IsNullOrWhiteSpace(context.AuthToken))
                message.Headers.TryAddWithoutValidation(MultipartSnapshotTransport.AuthenticationHeaderName, context.AuthToken);
            using HttpResponseMessage response = await http.SendAsync(message,
                exchange == Exchange.Download ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                DetectSessionExpired(body);
                return ClashOfRimClientNetworkResult<TResponse>.Failed("HttpError", $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }
            TResponse parsed = await read(response.Content, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return ClashOfRimClientNetworkResult<TResponse>.Ok(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or SerializationException or IOException)
        { return ClashOfRimClientNetworkResult<TResponse>.Failed(ex.GetType().Name, ex.Message); }
        finally
        {
            if (exchange == Exchange.Multipart) DisposeMultipart(message);
            else message?.Dispose();
        }
    }

    private async Task<T> ReadJson<T>(HttpContent content, CancellationToken token)
    {
        string body = await content.ReadAsStringAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        T response = ClientProtocolJson.Deserialize<T>(body, environment.DeserializationFailure);
        object? result = typeof(T).GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(response, null);
        if (result is ModProtocolResponseDto { Accepted: false } protocol) DetectSessionExpired(protocol.Message);
        return response;
    }

    private void DetectSessionExpired(string? message)
    {
        // Preserve the existing wire fallback until the server exposes an explicit session-expiry code.
        if (!string.IsNullOrWhiteSpace(message)
            && (message!.IndexOf("会话已过期", StringComparison.Ordinal) >= 0
                || message.IndexOf("session has expired", StringComparison.OrdinalIgnoreCase) >= 0))
            environment.SessionExpired(message, context.AuthToken);
    }

    private static HttpContent JsonContent<T>(T request) => new StringContent(ClientProtocolJson.Serialize(request), Encoding.UTF8, "application/json");

    private HttpContent MultipartContent<T>(T request, byte[] payload)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(JsonContent(request), "request");
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
