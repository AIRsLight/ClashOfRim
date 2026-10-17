namespace AIRsLight.ClashOfRim.Modules.Transport;

// Engine metadata and user-facing diagnostics stay outside the transport implementation.
internal interface IClientProtocolMetadata
{
    string? CurrentLanguage { get; }
    string DeserializationFailure(string responseType);
}

internal interface IClientProtocolEnvironment : IClientProtocolMetadata
{
    void SessionExpired(string message, string? authToken);
    void MultipartDisposalFailed(string message);
}
