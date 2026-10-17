using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshBankStatus() => _ = bankOperations.RefreshAsync();
    internal void StartCreateBankLoan(int principalSilver, int durationDays) => _ = bankOperations.BorrowAsync(principalSilver, durationDays);
    internal void StartRepayBankLoan() => _ = bankOperations.RepayLoanAsync();
    internal void StartRepayBankDebt(ModBankDebtSummaryDto debt) => _ = bankOperations.RepayDebtAsync(debt?.DebtId);

    internal void StartMercenarySnapshotConfirmation() => _ = mercenaryConfirmation.ConfirmAsync();
    internal void StartMercenarySnapshotConfirmation(string? completedContractId, string? completionIdempotencyKey = null)
        => _ = mercenaryConfirmation.ConfirmAsync(completedContractId, completionIdempotencyKey);

    internal void StartHireMercenary(string skill, int level, int days) => _ = mercenaryHiring.StartAsync(new(skill, level, days));
    internal void StartHireMercenaryGuard(string tier) => _ = mercenaryHiring.StartAsync(new("", 0, 0, tier));

    internal void StartQuoteMercenaryPrice(string requestKey, string skillDefName, int skillLevel, int durationDays,
        Action<string, string, ModMercenaryQuoteResponseDto?> callback)
        => _ = mercenaryQuotes.QuoteAsync(requestKey, skillDefName, skillLevel, durationDays, callback);

    internal void StartQuoteMercenaryGuardPrice(string requestKey, string tier,
        Action<string, string, ModMercenaryGuardQuoteResponseDto?> callback)
        => _ = mercenaryQuotes.QuoteGuardAsync(requestKey, tier, callback);

    internal void StartReportMercenaryIncident(string contractId, string incidentKind, string? idempotencyKeyOverride = null)
        => _ = mercenaryIncidents.ReportAsync(contractId, incidentKind, idempotencyKeyOverride);
}
