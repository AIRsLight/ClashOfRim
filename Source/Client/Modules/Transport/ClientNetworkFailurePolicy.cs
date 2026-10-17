using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientNetworkFailurePolicy
{
    internal static bool CanRetry(Exception exception) =>
        exception is ClientProtocolRequestException request ? CanRetry(request.Code, request.ServerMessage)
            : exception is HttpRequestException or TaskCanceledException or IOException or ObjectDisposedException;

    internal static bool CanRetry(string? code, string? message) =>
        (code == "HttpError" && IsTransientHttp(message))
        || code is nameof(HttpRequestException) or nameof(TaskCanceledException) or nameof(IOException) or nameof(ObjectDisposedException);

    private static bool IsTransientHttp(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string trimmed = message!.TrimStart();
        return trimmed.StartsWith("408 ", StringComparison.Ordinal)
            || trimmed.StartsWith("429 ", StringComparison.Ordinal)
            || trimmed.StartsWith("5", StringComparison.Ordinal);
    }
}
