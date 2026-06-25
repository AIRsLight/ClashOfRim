namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ClashOfRimClientNetworkResult<T>
{
    private ClashOfRimClientNetworkResult(
        bool success,
        T? response,
        string? errorCode,
        string? message)
    {
        Success = success;
        Response = response;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Success { get; }

    public T? Response { get; }

    public string? ErrorCode { get; }

    public string? Message { get; }

    public static ClashOfRimClientNetworkResult<T> Ok(T response)
    {
        return new ClashOfRimClientNetworkResult<T>(true, response, null, null);
    }

    public static ClashOfRimClientNetworkResult<T> Failed(string errorCode, string message)
    {
        return new ClashOfRimClientNetworkResult<T>(false, default, errorCode, message);
    }
}
