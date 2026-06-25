namespace AIRsLight.ClashOfRim.Save;

public sealed record ObservationSession(
    string SessionId,
    ReadOnlyObservationRequest Request,
    LatestSnapshotRecord Snapshot,
    MapSummary Map,
    ObservationModeRestrictions Restrictions)
{
    public bool CanSubmitSnapshot => false;

    public bool IsActionAllowed(ObservationAction action)
    {
        return Restrictions.IsAllowed(action);
    }

    public SnapshotUploadContext CreateUploadContextForSubmission()
    {
        throw new InvalidOperationException("Read-only observation sessions cannot create a snapshot upload context for server submission.");
    }
}
