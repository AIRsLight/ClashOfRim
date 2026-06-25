using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public static class RaidSettlementLedgerRecorder
{
    public static RaidSettlementLedgerRecordResult Record(
        IAuthoritativeEventLedger ledger,
        string sourceRaidEventId,
        RaidSettlementReturnResult settlementResult,
        DateTimeOffset recordedAtUtc,
        bool defenderOnline)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRaidEventId);
        ArgumentNullException.ThrowIfNull(settlementResult);

        AuthoritativeEvent? sourceRaid = ledger.Find(sourceRaidEventId);
        if (sourceRaid == null)
        {
            return new RaidSettlementLedgerRecordResult(
                RaidSettlementLedgerRecordResultKind.SourceRaidNotFound,
                SourceRaid: null,
                SettlementEvent: null,
                Created: false,
                FailureReason: ServerLocalization.Text("Raid.SettlementSourceNotFound"));
        }

        if (sourceRaid.Type != ServerEventType.Raid || sourceRaid.Payload is not RaidEventPayload sourcePayload)
        {
            return new RaidSettlementLedgerRecordResult(
                RaidSettlementLedgerRecordResultKind.SourceEventNotRaid,
                sourceRaid,
                SettlementEvent: null,
                Created: false,
                FailureReason: ServerLocalization.Text("Raid.SettlementSourceNotRaid"));
        }

        if (!sourcePayload.RequiresSettlement)
        {
            return new RaidSettlementLedgerRecordResult(
                RaidSettlementLedgerRecordResultKind.SourceRaidDoesNotRequireSettlement,
                sourceRaid,
                SettlementEvent: null,
                Created: false,
                FailureReason: ServerLocalization.Text("Raid.SettlementNotRequired"));
        }

        if (!settlementResult.Accepted || settlementResult.Settlement == null)
        {
            string failureReason = ServerLocalization.Text(
                "Raid.SettlementReturnRejected",
                new Dictionary<string, string?> { ["KIND"] = settlementResult.Kind.ToString() });
            AuthoritativeEvent updated = ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.NeedsManualReview,
                failureReason,
                nextRetryAtUtc: null);

            return new RaidSettlementLedgerRecordResult(
                RaidSettlementLedgerRecordResultKind.SettlementRejectedRecorded,
                updated,
                SettlementEvent: null,
                Created: false,
                failureReason);
        }

        string originalSnapshotId = settlementResult.OriginalSnapshot?.SnapshotId
            ?? sourcePayload.DefenderSnapshotId;
        string returnedSnapshotId = settlementResult.ReturnedSnapshot?.SnapshotId
            ?? "unknown-returned-snapshot";

        RaidSettlementRecord settlement = RaidSettlementRecord.FromDiff(
            originalSnapshotId,
            returnedSnapshotId,
            settlementResult.Settlement);

        string settlementIdempotencyKey = $"raid-settlement:{sourceRaid.EventId}";
        AuthoritativeEvent? existingSettlement = ledger.FindByIdempotencyKey(settlementIdempotencyKey)
            ?? ledger.ListAll().FirstOrDefault(evt =>
                evt.Type == ServerEventType.Raid
                && evt.Payload is RaidEventPayload { Settlement: not null }
                && evt.IdempotencyKey.StartsWith(settlementIdempotencyKey + ":", StringComparison.Ordinal));
        if (existingSettlement is not null)
        {
            return new RaidSettlementLedgerRecordResult(
                RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists,
                sourceRaid,
                existingSettlement,
                Created: false,
                FailureReason: null);
        }

        AuthoritativeEvent settlementEvent = AuthoritativeEventFactory.Create(
            ServerEventType.Raid,
            sourceRaid.Actor,
            sourceRaid.Target,
            settlementIdempotencyKey,
            defenderOnline,
            new RaidEventPayload(
                originalSnapshotId,
                returnedSnapshotId,
                sourcePayload.StartedAtUtc,
                recordedAtUtc,
                settlement),
            recordedAtUtc,
            sourceRaid.TargetContext);

        LedgerAppendResult appendResult = ledger.Append(settlementEvent);
        return new RaidSettlementLedgerRecordResult(
            appendResult.Created
                ? RaidSettlementLedgerRecordResultKind.SettlementEventCreated
                : RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists,
            sourceRaid,
            appendResult.Event,
            appendResult.Created,
            FailureReason: null);
    }
}
