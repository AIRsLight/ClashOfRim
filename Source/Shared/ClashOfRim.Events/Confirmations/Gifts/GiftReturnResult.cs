namespace AIRsLight.ClashOfRim.Events;

public sealed record GiftReturnResult(
    AuthoritativeEvent RejectedGift,
    AuthoritativeEvent ReturnEvent,
    bool ReturnEventCreated);
