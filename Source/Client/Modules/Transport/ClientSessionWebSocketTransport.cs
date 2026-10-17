using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal sealed class ClientSessionWebSocketTransport : IClientSessionStreamTransport
{
    private static readonly TimeSpan SessionWebSocketPingInterval = TimeSpan.FromSeconds(10);
    private static readonly string StreamSessionRoute = ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).Route;
    private readonly IClientWebSocketFactory sockets;
    private readonly IClientProtocolMetadata protocolEnvironment;
    private readonly TimeSpan closeTimeout;

    internal ClientSessionWebSocketTransport(IClientWebSocketFactory sockets, IClientProtocolMetadata environment, TimeSpan? closeTimeout = null)
    {
        this.sockets = sockets;
        protocolEnvironment = environment;
        this.closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(
        ClashOfRimClientNetworkContext context,
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion,
        Action<ModSessionStreamEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        CancellationToken owner = ClientSessionWork.Token;
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner);
        cancellationToken = sessionCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = BuildWebSocketSessionUri(context, sessionId, knownNotificationVersion, knownWorldConfigurationVersion);
            string? language = protocolEnvironment.CurrentLanguage;
            using var webSocket = await sockets.OpenAsync(uri,
                string.IsNullOrWhiteSpace(language) ? null : ServerLocalization.EncodeLanguageHeader(language), cancellationToken).ConfigureAwait(false);
            using var abortOnCancellation = cancellationToken.Register(webSocket.Abort);
            await RunDuplexAsync(webSocket, owner, onEvent, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await CompleteCloseAsync(webSocket, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new ModSessionStreamClosedDto(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException or SerializationException or TimeoutException)
        {
            if (cancellationToken.IsCancellationRequested || owner != ClientSessionWork.Token || !ClientSessionWork.IsCurrent)
            {
                return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new ModSessionStreamClosedDto(true));
            }

            return ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private async Task RunDuplexAsync(WebSocket socket, CancellationToken owner, Action<ModSessionStreamEvent> onEvent, CancellationToken token)
    {
        using var io = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task receive = ReceiveEventsAsync(socket, owner, onEvent, io.Token);
        Task ping = SendWebSocketPingLoopAsync(socket, io.Token);
        Task completed = await Task.WhenAny(receive, ping).ConfigureAwait(false);
        try { await completed.ConfigureAwait(false); }
        finally
        {
            // Either direction ending owns shutdown of the other. Observe both before disposal;
            // a secondary failure during abort must not replace the original failure.
            if (!completed.IsCompletedSuccessfully) socket.Abort();
            io.Cancel();
            Task peer = ReferenceEquals(completed, receive) ? ping : receive;
            try { await peer.ConfigureAwait(false); }
            catch (Exception) when (!completed.IsCompletedSuccessfully || peer.IsCanceled && io.IsCancellationRequested) { }
        }
    }

    private async Task ReceiveEventsAsync(WebSocket socket, CancellationToken owner, Action<ModSessionStreamEvent> onEvent, CancellationToken token)
    {
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
        {
            token.ThrowIfCancellationRequested();
            string? message = await ReceiveWebSocketTextAsync(socket, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (message is null) return;
            var envelope = ClientProtocolJson.Deserialize<ModSessionWebSocketEnvelopeDto>(message, protocolEnvironment.DeserializationFailure);
            if (string.Equals(envelope.EventName, "pong", StringComparison.Ordinal)
                || string.Equals(envelope.EventName, "keepalive", StringComparison.Ordinal)) continue;

            using (ClientSessionWork.EnterPublication())
            {
                token.ThrowIfCancellationRequested();
                if (owner != ClientSessionWork.Token) throw new OperationCanceledException("The multiplayer session ended.", owner);
                onEvent(new ModSessionStreamEvent(envelope.EventName, envelope.Data));
            }
        }
    }

    private async Task CompleteCloseAsync(WebSocket socket, CancellationToken token)
    {
        if (socket.State != WebSocketState.CloseReceived) return;
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        cleanup.CancelAfter(closeTimeout);
        try
        {
            // The receive loop already consumed the peer's close frame. Only send our reply.
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client closed.", cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            socket.Abort();
            throw new TimeoutException("The session WebSocket close reply timed out.");
        }
    }

    private static async Task SendWebSocketPingLoopAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            await SendWebSocketTextAsync(webSocket, "{\"eventName\":\"ping\",\"data\":\"{}\"}", cancellationToken).ConfigureAwait(false);
            await Task.Delay(SessionWebSocketPingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendWebSocketTextAsync(WebSocket webSocket, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveWebSocketTextAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("The session WebSocket requires text messages.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildStreamSessionRoute(
        ClashOfRimClientNetworkContext context,
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion)
    {
        var query = new List<string>
        {
            "userId=" + Uri.EscapeDataString(context.UserId),
            "colonyId=" + Uri.EscapeDataString(context.ColonyId),
            "knownNotificationVersion=" + knownNotificationVersion.ToString(CultureInfo.InvariantCulture),
            "knownWorldConfigurationVersion=" + knownWorldConfigurationVersion.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
        {
            query.Add("currentSnapshotId=" + Uri.EscapeDataString(context.CurrentSnapshotId));
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            query.Add("sessionId=" + Uri.EscapeDataString(sessionId));
        }

        return StreamSessionRoute.TrimStart('/') + "?" + string.Join("&", query);
    }

    private static Uri BuildWebSocketSessionUri(
        ClashOfRimClientNetworkContext context,
        string? sessionId,
        long knownNotificationVersion,
        long knownWorldConfigurationVersion)
    {
        Uri baseUri = ClashOfRimServerUrlUtility.BuildHttpBaseUri(context.ServerBaseUrl);
        Uri httpUri = new(baseUri, BuildStreamSessionRoute(context, sessionId, knownNotificationVersion, knownWorldConfigurationVersion));
        var builder = new UriBuilder(httpUri)
        {
            Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }
}
