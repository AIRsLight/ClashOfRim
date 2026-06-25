namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ProtocolResponse
{
    public ProtocolResponse(bool accepted, ProtocolErrorCode errorCode, string? message)
    {
        Accepted = accepted;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Accepted { get; }

    public ProtocolErrorCode ErrorCode { get; }

    public string? Message { get; }

    public static ProtocolResponse Ok(string? message = null)
    {
        return new ProtocolResponse(true, ProtocolErrorCode.None, message);
    }

    public static ProtocolResponse Reject(ProtocolErrorCode errorCode, string message)
    {
        return new ProtocolResponse(false, errorCode, message);
    }
}
