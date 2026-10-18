using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Transport;

// Engine metadata and user-facing diagnostics stay outside the transport implementation.
internal interface IClientProtocolMetadata
{
    string? CurrentLanguage { get; }
    string DeserializationFailure(string responseType);
}

internal interface IClientProtocolEnvironment : IClientProtocolMetadata
{
    Action? CaptureSessionUnavailable(ClashOfRimClientNetworkContext context, string? sessionId);
    void MultipartDisposalFailed(string message);
}
