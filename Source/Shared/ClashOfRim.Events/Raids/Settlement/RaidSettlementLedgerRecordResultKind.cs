namespace AIRsLight.ClashOfRim.Events;

public enum RaidSettlementLedgerRecordResultKind
{
    SettlementEventCreated,
    SettlementEventAlreadyExists,
    SettlementRejectedRecorded,
    SourceRaidNotFound,
    SourceEventNotRaid,
    SourceRaidDoesNotRequireSettlement
}
