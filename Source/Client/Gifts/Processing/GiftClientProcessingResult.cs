namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftClientProcessingResult
{
    private GiftClientProcessingResult(
        GiftClientProcessingResultKind kind,
        GiftLandingPlan? landingPlan,
        GiftRejectionRequest? rejectionRequest,
        string? message)
    {
        Kind = kind;
        LandingPlan = landingPlan;
        RejectionRequest = rejectionRequest;
        Message = message;
    }

    public GiftClientProcessingResultKind Kind { get; }

    public GiftLandingPlan? LandingPlan { get; }

    public GiftRejectionRequest? RejectionRequest { get; }

    public string? Message { get; }

    public bool Success =>
        Kind == GiftClientProcessingResultKind.AcceptedLandingPlanCreated ||
        Kind == GiftClientProcessingResultKind.RejectRequestCreated;

    public static GiftClientProcessingResult Accepted(GiftLandingPlan landingPlan)
    {
        return new GiftClientProcessingResult(
            GiftClientProcessingResultKind.AcceptedLandingPlanCreated,
            landingPlan,
            null,
            ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusAcceptPlanCreated"));
    }

    public static GiftClientProcessingResult Rejected(GiftRejectionRequest rejectionRequest)
    {
        return new GiftClientProcessingResult(
            GiftClientProcessingResultKind.RejectRequestCreated,
            null,
            rejectionRequest,
            ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusRejectPlanCreated"));
    }

    public static GiftClientProcessingResult Failed(GiftClientProcessingResultKind kind, string message)
    {
        return new GiftClientProcessingResult(kind, null, null, message);
    }
}
