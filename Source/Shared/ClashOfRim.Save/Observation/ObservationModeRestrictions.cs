namespace AIRsLight.ClashOfRim.Save;

public sealed record ObservationModeRestrictions(IReadOnlySet<ObservationAction> ProhibitedActions)
{
    public static ObservationModeRestrictions ServerIsolatedSandbox { get; } = new(
        new HashSet<ObservationAction>
        {
            ObservationAction.InjectObservationPawn,
            ObservationAction.EnterWithRealColonist,
            ObservationAction.UploadSnapshot
        });

    public bool IsAllowed(ObservationAction action)
    {
        return !ProhibitedActions.Contains(action);
    }
}
