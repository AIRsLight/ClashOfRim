using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal interface IClientWebSocketFactory
{
    // The factory owns failed connections; ownership transfers to the caller only on success.
    Task<WebSocket> OpenAsync(Uri uri, string? encodedLanguage, CancellationToken cancellationToken);
}
