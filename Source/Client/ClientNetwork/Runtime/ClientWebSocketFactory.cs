using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientWebSocketFactory : IClientWebSocketFactory
{
    public async Task<WebSocket> OpenAsync(Uri uri, string? encodedLanguage, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            if (!string.IsNullOrWhiteSpace(encodedLanguage))
                socket.Options.SetRequestHeader(ClientProtocolTransport.LanguageHeader, encodedLanguage);
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
}
