using System;
using System.Net.Http;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

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
    {
        if (string.IsNullOrWhiteSpace(contractId) || string.IsNullOrWhiteSpace(incidentKind))
        {
            return;
        }

        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            mercenaryState.Message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusNotConfigured");
            return;
        }

        mercenaryState.Message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusReportingIncident");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        bool isCompletionIncident = string.Equals(incidentKind, "Completed", StringComparison.Ordinal);
        string idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKeyOverride)
            ? $"mercenary-incident:{settings.UserId}:{settings.ColonyId}:{contractId}:{incidentKind}:{DateTime.UtcNow.Ticks}"
            : idempotencyKeyOverride!;
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto> result =
                    await ClientSessionWork.Observe(client.ReportMercenaryIncidentAsync(idempotencyKey, currentTicks, contractId, incidentKind));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    mercenaryState.Message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryState.Message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    bankState.View = result.Response.BankStatus;
                    return;
                }

                bankState.View = result.Response.BankStatus;
                mercenaryState.Message = result.Response.Debt is null
                    ? ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentReportedNoDebt")
                    : ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentReported", result.Response.Debt.AmountSilver.Named("FINE"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (result.Response.Debt is not null && result.Response.BankStatus is not null)
                    {
                        ClashBankLoanQuestUtility.CreateOrUpdateDebtQuest(result.Response.Debt, result.Response.BankStatus);
                    }

                    if (isCompletionIncident)
                    {
                        StartRefreshBankStatus();
                        return;
                    }

                    Messages.Message(mercenaryState.Message, MessageTypeDefOf.NegativeEvent, historical: false);
                    StartMercenarySnapshotConfirmation();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                mercenaryState.Message = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary incident report failed: " + ex);
            }
        });
    }


}
