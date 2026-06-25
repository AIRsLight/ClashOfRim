namespace AIRsLight.ClashOfRim.Save;

public sealed record ObservationLoadResult(
    ObservationLoadResultKind Kind,
    string Message,
    ObservationSession? Session = null)
{
    public bool Granted => Kind == ObservationLoadResultKind.Granted;

    public static ObservationLoadResult Reject(ObservationLoadResultKind kind, string message)
    {
        if (kind == ObservationLoadResultKind.Granted)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Granted results must include an observation session.");
        }

        return new ObservationLoadResult(kind, message);
    }

    public static ObservationLoadResult Grant(ObservationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new ObservationLoadResult(ObservationLoadResultKind.Granted, "Read-only observation load granted.", session);
    }
}
