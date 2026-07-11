namespace AIRsLight.ClashOfRim.Gifts;

public sealed class ItemDeliveryClientProcessingResult
{
    private ItemDeliveryClientProcessingResult(
        ItemDeliveryClientProcessingResultKind kind,
        GiftLandingPlan? landingPlan,
        GiftRejectionRequest? rejectionRequest,
        string? message)
    {
        Kind = kind;
        LandingPlan = landingPlan;
        RejectionRequest = rejectionRequest;
        Message = message;
    }

    public ItemDeliveryClientProcessingResultKind Kind { get; }

    public GiftLandingPlan? LandingPlan { get; }

    public GiftRejectionRequest? RejectionRequest { get; }

    public string? Message { get; }

    public bool Success =>
        Kind == ItemDeliveryClientProcessingResultKind.AcceptedLandingPlanCreated ||
        Kind == ItemDeliveryClientProcessingResultKind.RejectRequestCreated;

    public static ItemDeliveryClientProcessingResult Accepted(GiftLandingPlan landingPlan)
    {
        return new ItemDeliveryClientProcessingResult(
            ItemDeliveryClientProcessingResultKind.AcceptedLandingPlanCreated,
            landingPlan,
            null,
            ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusAcceptPlanCreated"));
    }

    public static ItemDeliveryClientProcessingResult Rejected(GiftRejectionRequest rejectionRequest)
    {
        return new ItemDeliveryClientProcessingResult(
            ItemDeliveryClientProcessingResultKind.RejectRequestCreated,
            null,
            rejectionRequest,
            ClashOfRimText.Key("ClashOfRim.GiftProcessing.StatusRejectPlanCreated"));
    }

    public static ItemDeliveryClientProcessingResult Failed(ItemDeliveryClientProcessingResultKind kind, string message)
    {
        return new ItemDeliveryClientProcessingResult(kind, null, null, message);
    }
}
