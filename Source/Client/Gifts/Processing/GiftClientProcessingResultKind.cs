namespace AIRsLight.ClashOfRim.Gifts;

public enum GiftClientProcessingResultKind
{
    AcceptedLandingPlanCreated,
    RejectRequestCreated,
    NotGiftEvent,
    MissingPayload,
    PayloadParseFailed,
    MissingTargetMap,
    MissingIdentity
}
