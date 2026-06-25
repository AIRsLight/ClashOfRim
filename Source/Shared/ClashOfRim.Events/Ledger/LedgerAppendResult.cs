namespace AIRsLight.ClashOfRim.Events;

public sealed record LedgerAppendResult(AuthoritativeEvent Event, bool Created);
