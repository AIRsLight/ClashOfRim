namespace AIRsLight.ClashOfRim.Pawns;

internal readonly struct PawnPackageTransferResult
{
    private PawnPackageTransferResult(bool success, string message)
    {
        Success = success;
        Message = message ?? string.Empty;
    }

    public bool Success { get; }

    public string Message { get; }

    public static PawnPackageTransferResult Ok()
    {
        return new PawnPackageTransferResult(true, string.Empty);
    }

    public static PawnPackageTransferResult Failed(string message)
    {
        return new PawnPackageTransferResult(false, message);
    }
}
