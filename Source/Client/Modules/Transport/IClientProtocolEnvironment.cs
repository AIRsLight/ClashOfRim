namespace AIRsLight.ClashOfRim.Modules.Transport;

// Engine metadata and user-facing diagnostics stay outside the transport implementation.
internal interface IClientProtocolEnvironment
{
    string? CurrentLanguage { get; }
    string DeserializationFailure(string responseType);
    void SessionExpired(string message, string? authToken);
    void MultipartDisposalFailed(string message);
}
