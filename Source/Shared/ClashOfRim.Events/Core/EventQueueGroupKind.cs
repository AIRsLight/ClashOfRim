namespace AIRsLight.ClashOfRim.Events;

public enum EventQueueGroupKind
{
    DirectlyProcessable,
    WaitingForConfirmation,
    DeliveredUnconfirmed,
    Conflict,
    Rejected
}
