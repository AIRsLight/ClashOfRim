using System;

namespace AIRsLight.ClashOfRim.Modules.Transport;

// Preserve the transport result when a sequential package upload reports failure by throwing.
internal sealed class ClientProtocolRequestException : Exception
{
    internal ClientProtocolRequestException(string message, string? code, string? serverMessage) : base(message)
    { Code = code; ServerMessage = serverMessage; }
    internal string? Code { get; }
    internal string? ServerMessage { get; }
}
