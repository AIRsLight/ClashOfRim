namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidSettlementLedgerRecordResult(
    RaidSettlementLedgerRecordResultKind Kind,
    AuthoritativeEvent? SourceRaid,
    AuthoritativeEvent? SettlementEvent,
    bool Created,
    string? FailureReason);
