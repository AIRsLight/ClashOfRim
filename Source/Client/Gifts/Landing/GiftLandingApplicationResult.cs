namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftLandingApplicationResult
{
    private GiftLandingApplicationResult(
        GiftLandingApplicationResultKind kind,
        string eventId,
        int placedThingCount,
        int placedStackCount,
        string landingMode,
        bool requiresSnapshotConfirmation,
        string message)
    {
        Kind = kind;
        EventId = eventId;
        PlacedThingCount = placedThingCount;
        PlacedStackCount = placedStackCount;
        LandingMode = landingMode;
        RequiresSnapshotConfirmation = requiresSnapshotConfirmation;
        Message = message;
    }

    public GiftLandingApplicationResultKind Kind { get; }

    public string EventId { get; }

    public int PlacedThingCount { get; }

    public int PlacedStackCount { get; }

    public string LandingMode { get; }

    public bool RequiresSnapshotConfirmation { get; }

    public string Message { get; }

    public bool Success => Kind == GiftLandingApplicationResultKind.Applied;

    public static GiftLandingApplicationResult Applied(
        string eventId,
        int placedThingCount,
        int placedStackCount,
        string landingMode,
        bool requiresSnapshotConfirmation)
    {
        return new GiftLandingApplicationResult(
            GiftLandingApplicationResultKind.Applied,
            eventId,
            placedThingCount,
            placedStackCount,
            landingMode,
            requiresSnapshotConfirmation,
            ClashOfRimText.Key("ClashOfRim.GiftLanding.StatusApplied"));
    }

    public static GiftLandingApplicationResult Failed(
        GiftLandingApplicationResultKind kind,
        string eventId,
        string message)
    {
        return new GiftLandingApplicationResult(
            kind,
            eventId,
            placedThingCount: 0,
            placedStackCount: 0,
            landingMode: string.Empty,
            requiresSnapshotConfirmation: false,
            message);
    }
}
