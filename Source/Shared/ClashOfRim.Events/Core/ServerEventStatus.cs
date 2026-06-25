namespace AIRsLight.ClashOfRim.Events;

public enum ServerEventStatus
{
    Recorded,
    ReadyForImmediateDelivery,
    PendingOfflineDelivery,
    DeliveredToClient,
    AppliedToSnapshot,
    RejectedByTarget,
    Conflict,
    Cancelled,
    Failed
}
