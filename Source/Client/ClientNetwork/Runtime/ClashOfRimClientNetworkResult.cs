namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ClashOfRimClientNetworkResult<T>
{
    private ClashOfRimClientNetworkResult(
        bool success,
        T? response,
        string? errorCode,
        string? message, string? serverProtocolVersion = null)
    {
        Success = success;
        Response = response;
        ErrorCode = errorCode;
        Message = message;
        ServerProtocolVersion = serverProtocolVersion;
    }

    public bool Success { get; }

    public T? Response { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }
    public string? ServerProtocolVersion { get; }

    public static ClashOfRimClientNetworkResult<T> Ok(T response)
    {
        return new ClashOfRimClientNetworkResult<T>(true, response, null, null);
    }

    public static ClashOfRimClientNetworkResult<T> Failed(string errorCode, string message)
        => Failed(errorCode, message, null);

    public static ClashOfRimClientNetworkResult<T> Failed(string errorCode, string message, string? serverProtocolVersion)
    {
        return new ClashOfRimClientNetworkResult<T>(false, default, errorCode, message, serverProtocolVersion);
    }
}
