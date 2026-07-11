namespace AIRsLight.ClashOfRim.Gifts;

public enum ItemDeliveryClientProcessingResultKind
{
    AcceptedLandingPlanCreated,
    RejectRequestCreated,
    NotItemDeliveryEvent,
    MissingPayload,
    PayloadParseFailed,
    MissingTargetMap,
    MissingIdentity
}
