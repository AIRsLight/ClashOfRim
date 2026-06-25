using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshBankStatus()
    {
        if (!CanRunManualSync(out string failureReason))
        {
            bankStatus = failureReason;
            return;
        }

        if (bankInProgress)
        {
            return;
        }

        bankInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRefreshing");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        int colonyWealth = CalculateCurrentColonyWealth();
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModBankStatusResponseDto> result =
                    await client.GetBankStatusAsync(currentTicks, colonyWealth);
                if (!result.Success || result.Response is null)
                {
                    bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRefreshFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRefreshRejected", result.Response.Result.ErrorCode.Named("CODE"), result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                lastBankStatus = result.Response;
                bankStatus = BankUiUtility.FormatStatus(result.Response);
                ModBankStatusResponseDto statusSnapshot = result.Response;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    ClashBankLoanQuestUtility.CreateOrUpdateDebtQuests(statusSnapshot));
            }
            catch (Exception ex)
            {
                bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Bank status refresh failed: " + ex);
            }
            finally
            {
                bankInProgress = false;
            }
        });
    }

    internal void StartCreateBankLoan(int principalSilver, int durationDays)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            bankStatus = atomicMessage;
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            bankStatus = failureReason;
            return;
        }

        if (bankInProgress || manualSyncInProgress || snapshotUploadInProgress)
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy");
            return;
        }

        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusNoMap");
            return;
        }

        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusCreating");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        int colonyWealth = CalculateCurrentColonyWealth();
        string idempotencyKey = $"bank-loan:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        ModBankStatusResponseDto? bankStatusSnapshot = lastBankStatus;
        if (bankStatusSnapshot is null)
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusNotPulled");
            manualSyncInProgress = false;
            bankInProgress = false;
            return;
        }

        int maxLoanSilver = Math.Max(0, (int)Math.Floor(Math.Max(0, colonyWealth) * bankStatusSnapshot.MaxLoanWealthRatio));
        if (principalSilver <= 0 || principalSilver > maxLoanSilver)
        {
            bankStatus = ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusCreateRejected",
                "ValidationFailed".Named("CODE"),
                ClashOfRimText.Key("ClashOfRim.Bank.StatusCreateMaxLoanExceeded", maxLoanSilver.Named("MAX")).Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            return;
        }

        if (durationDays < bankStatusSnapshot.MinDurationDays || durationDays > bankStatusSnapshot.MaxDurationDays)
        {
            bankStatus = ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusCreateRejected",
                "ValidationFailed".Named("CODE"),
                ClashOfRimText.Key(
                    "ClashOfRim.Bank.StatusCreateDurationInvalid",
                    bankStatusSnapshot.MinDurationDays.Named("MIN"),
                    bankStatusSnapshot.MaxDurationDays.Named("MAX")).Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            return;
        }

        string loanId = "bankloan-" + Guid.NewGuid().ToString("N");
        int interestSilver = BankUiUtility.CalculateInterestSilver(principalSilver, durationDays, bankStatusSnapshot);
        int totalDueSilver = principalSilver + interestSilver;
        long dueAtGameTicks = currentTicks + durationDays * (long)ClashManagedQuestTimingUtility.TicksPerDay;
        var loan = new ModBankLoanSummaryDto
        {
            LoanId = loanId,
            PrincipalSilver = principalSilver,
            InterestSilver = interestSilver,
            TotalDueSilver = totalDueSilver,
            DurationDays = durationDays,
            CreatedAtGameTicks = currentTicks,
            DueAtGameTicks = dueAtGameTicks,
            PenaltyRaidPoints = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalDueSilver) * bankStatusSnapshot.PenaltyRaidPointsPerSilver)),
            Status = "Active",
            SourceKind = "Loan"
        };
        var pendingStatus = CloneBankStatusWithActiveLoan(bankStatusSnapshot, loan);
        BeginLocalAtomicMutation(
            ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank"),
            ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        if (!TryDropBankLoanSilverOnPlayerMap(loan, idempotencyKey, map, out string placeMessage))
        {
            ClearLocalAtomicMutation();
            bankStatus = placeMessage;
            manualSyncInProgress = false;
            bankInProgress = false;
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        ClashBankLoanQuestUtility.CreateOrUpdateLoanQuest(loan, pendingStatus);
        lastBankStatus = pendingStatus;
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankLoanTransaction(
                    idempotencyKey,
                    currentTicks,
                    colonyWealth,
                    loan,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankLoanTransaction(
            idempotencyKey,
            currentTicks,
            colonyWealth,
            loan,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    private void StartBuildAndSubmitPreparedBankLoanTransaction(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        ModBankLoanSummaryDto loan,
        ModBankStatusResponseDto pendingStatus)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            bankStatus = failureReason;
            ShowUnconfirmedSnapshotFailure(
                operation,
                failureReason,
                () => StartBuildAndSubmitPreparedBankLoanTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    colonyWealth,
                    loan,
                    pendingStatus));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankLoanTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    colonyWealth,
                    loan,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankLoanTransaction(
            idempotencyKey,
            currentGameTicks,
            colonyWealth,
            loan,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitPreparedBankLoanTransaction(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        ModBankLoanSummaryDto loan,
        ModBankStatusResponseDto pendingStatus,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy");
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModBankLoanResponseDto> result =
                    await client.CreateBankLoanWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        colonyWealth,
                        loan.PrincipalSilver,
                        loan.DurationDays,
                        loan.LoanId,
                        loan.InterestSilver,
                        confirmedSnapshot,
                        confirmedPayload);
                if (!result.Success || result.Response is null)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusConfirmationFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    bankStatus = status;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankLoanTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            colonyWealth,
                            loan,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusCreateRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    bankStatus = status;
                    if (result.Response.Status is not null)
                    {
                        lastBankStatus = result.Response.Status;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankLoanTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            colonyWealth,
                            loan,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    ModBankLoanSummaryDto confirmedLoan = result.Response.Loan ?? loan;
                    ModBankStatusResponseDto confirmedStatus = result.Response.Status
                        ?? CloneBankStatusWithActiveLoan(pendingStatus, confirmedLoan);
                    lastBankStatus = confirmedStatus;
                    ClashBankLoanQuestUtility.CreateOrUpdateLoanQuest(confirmedLoan, confirmedStatus);
                    bankStatus = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusCreated",
                        confirmedLoan.PrincipalSilver.Named("PRINCIPAL"),
                        confirmedLoan.TotalDueSilver.Named("TOTAL"));
                    Messages.Message(bankStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = ClashOfRimText.Key(
                    "ClashOfRim.Bank.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                bankStatus = status;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    status,
                    () => StartSubmitPreparedBankLoanTransaction(
                        idempotencyKey,
                        currentGameTicks,
                        colonyWealth,
                        loan,
                        pendingStatus,
                        confirmedSnapshot,
                        confirmedPayload));
                Log.Warning("[ClashOfRim] Bank loan transaction failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                bankInProgress = false;
            }
        });
    }

    internal void StartRepayBankLoan()
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            SetBankStatus(atomicMessage, MessageTypeDefOf.RejectInput);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            SetBankStatus(failureReason, MessageTypeDefOf.RejectInput);
            return;
        }

        ModBankLoanSummaryDto? activeLoan = lastBankStatus?.ActiveLoan;
        if (activeLoan is null)
        {
            SetBankStatus(ClashOfRimText.Key("ClashOfRim.Bank.StatusNoActiveLoan"), MessageTypeDefOf.RejectInput);
            return;
        }
        activeLoan = CloneLoanWithLocalTotalDue(activeLoan);

        if (bankInProgress || manualSyncInProgress || snapshotUploadInProgress)
        {
            SetBankStatus(ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy"), MessageTypeDefOf.RejectInput);
            return;
        }

        int availableSilver = CountPlayerHomeSilver();
        if (availableSilver < activeLoan.TotalDueSilver)
        {
            SetBankStatus(
                ClashOfRimText.Key("ClashOfRim.Bank.StatusInsufficientSilver", activeLoan.TotalDueSilver.Named("NEEDED"), availableSilver.Named("AVAILABLE")),
                MessageTypeDefOf.RejectInput);
            return;
        }

        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRepaying");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"bank-repay:{settings.UserId}:{settings.ColonyId}:{activeLoan.LoanId}:{DateTime.UtcNow.Ticks}";
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        if (!TryConsumePlayerHomeSilver(activeLoan.TotalDueSilver, out string consumeMessage))
        {
            ClearLocalAtomicMutation();
            bankStatus = consumeMessage;
            manualSyncInProgress = false;
            bankInProgress = false;
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        ClashBankLoanQuestUtility.MarkLoanRepaid(activeLoan.LoanId);
        ModBankStatusResponseDto pendingStatus = CloneBankStatusWithoutActiveLoan(lastBankStatus);
        lastBankStatus = pendingStatus;
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankLoanRepayment(
                    idempotencyKey,
                    currentTicks,
                    activeLoan,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankLoanRepayment(
            idempotencyKey,
            currentTicks,
            activeLoan,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    internal void StartRepayBankDebt(ModBankDebtSummaryDto debt)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            SetBankStatus(atomicMessage, MessageTypeDefOf.RejectInput);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            SetBankStatus(failureReason, MessageTypeDefOf.RejectInput);
            return;
        }

        if (debt is null || string.IsNullOrWhiteSpace(debt.DebtId))
        {
            SetBankStatus(ClashOfRimText.Key("ClashOfRim.Bank.StatusMissingDebt"), MessageTypeDefOf.RejectInput);
            return;
        }

        if (bankInProgress || manualSyncInProgress || snapshotUploadInProgress)
        {
            SetBankStatus(ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy"), MessageTypeDefOf.RejectInput);
            return;
        }

        int availableSilver = CountPlayerHomeSilver();
        if (availableSilver < debt.AmountSilver)
        {
            SetBankStatus(
                ClashOfRimText.Key("ClashOfRim.Bank.StatusInsufficientSilver", debt.AmountSilver.Named("NEEDED"), availableSilver.Named("AVAILABLE")),
                MessageTypeDefOf.RejectInput);
            return;
        }

        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRepayingDebt");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"bank-debt-repay:{settings.UserId}:{settings.ColonyId}:{debt.DebtId}:{DateTime.UtcNow.Ticks}";
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        if (!TryConsumePlayerHomeSilver(debt.AmountSilver, out string consumeMessage))
        {
            ClearLocalAtomicMutation();
            bankStatus = consumeMessage;
            manualSyncInProgress = false;
            bankInProgress = false;
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        ClashBankLoanQuestUtility.MarkDebtRepaid(debt.DebtId);
        ModBankStatusResponseDto pendingStatus = CloneBankStatusWithoutDebt(lastBankStatus, debt.DebtId);
        lastBankStatus = pendingStatus;
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankDebtRepayment(
                    idempotencyKey,
                    currentTicks,
                    debt,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankDebtRepayment(
            idempotencyKey,
            currentTicks,
            debt,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    private void StartBuildAndSubmitPreparedBankLoanRepayment(
        string idempotencyKey,
        long currentGameTicks,
        ModBankLoanSummaryDto loan,
        ModBankStatusResponseDto pendingStatus)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            bankStatus = failureReason;
            ShowUnconfirmedSnapshotFailure(
                operation,
                failureReason,
                () => StartBuildAndSubmitPreparedBankLoanRepayment(
                    idempotencyKey,
                    currentGameTicks,
                    loan,
                    pendingStatus));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankLoanRepayment(
                    idempotencyKey,
                    currentGameTicks,
                    loan,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankLoanRepayment(
            idempotencyKey,
            currentGameTicks,
            loan,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitPreparedBankLoanRepayment(
        string idempotencyKey,
        long currentGameTicks,
        ModBankLoanSummaryDto loan,
        ModBankStatusResponseDto pendingStatus,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy");
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModBankLoanResponseDto> result =
                    await client.RepayBankLoanWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        loan.LoanId,
                        loan.TotalDueSilver,
                        confirmedSnapshot,
                        confirmedPayload);
                if (!result.Success || result.Response is null)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusRepayFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    bankStatus = status;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankLoanRepayment(
                            idempotencyKey,
                            currentGameTicks,
                            loan,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusRepayRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    bankStatus = status;
                    if (result.Response.Status is not null)
                    {
                        lastBankStatus = result.Response.Status;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankLoanRepayment(
                            idempotencyKey,
                            currentGameTicks,
                            loan,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    lastBankStatus = result.Response.Status ?? pendingStatus;
                    bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusRepaid", loan.TotalDueSilver.Named("TOTAL"));
                    Messages.Message(bankStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = ClashOfRimText.Key(
                    "ClashOfRim.Bank.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                bankStatus = status;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    status,
                    () => StartSubmitPreparedBankLoanRepayment(
                        idempotencyKey,
                        currentGameTicks,
                        loan,
                        pendingStatus,
                        confirmedSnapshot,
                        confirmedPayload));
                Log.Warning("[ClashOfRim] Bank loan repayment transaction failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                bankInProgress = false;
            }
        });
    }

    private void StartBuildAndSubmitPreparedBankDebtRepayment(
        string idempotencyKey,
        long currentGameTicks,
        ModBankDebtSummaryDto debt,
        ModBankStatusResponseDto pendingStatus)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            bankStatus = failureReason;
            ShowUnconfirmedSnapshotFailure(
                operation,
                failureReason,
                () => StartBuildAndSubmitPreparedBankDebtRepayment(
                    idempotencyKey,
                    currentGameTicks,
                    debt,
                    pendingStatus));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        manualSyncInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            bankInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                bankStatus,
                () => StartBuildAndSubmitPreparedBankDebtRepayment(
                    idempotencyKey,
                    currentGameTicks,
                    debt,
                    pendingStatus));
            return;
        }

        StartSubmitPreparedBankDebtRepayment(
            idempotencyKey,
            currentGameTicks,
            debt,
            pendingStatus,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitPreparedBankDebtRepayment(
        string idempotencyKey,
        long currentGameTicks,
        ModBankDebtSummaryDto debt,
        ModBankStatusResponseDto pendingStatus,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            bankStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusBusy");
            Messages.Message(bankStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"));
        bankInProgress = true;
        bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModBankDebtResponseDto> result =
                    await client.RepayBankDebtWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        debt.DebtId,
                        debt.AmountSilver,
                        confirmedSnapshot,
                        confirmedPayload);
                if (!result.Success || result.Response is null)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusDebtRepayFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    bankStatus = status;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankDebtRepayment(
                            idempotencyKey,
                            currentGameTicks,
                            debt,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Bank.StatusDebtRepayRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    bankStatus = status;
                    if (result.Response.Status is not null)
                    {
                        lastBankStatus = result.Response.Status;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => StartSubmitPreparedBankDebtRepayment(
                            idempotencyKey,
                            currentGameTicks,
                            debt,
                            pendingStatus,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    lastBankStatus = result.Response.Status ?? pendingStatus;
                    bankStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusDebtRepaid", debt.AmountSilver.Named("TOTAL"));
                    Messages.Message(bankStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = ClashOfRimText.Key(
                    "ClashOfRim.Bank.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                bankStatus = status;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    status,
                    () => StartSubmitPreparedBankDebtRepayment(
                        idempotencyKey,
                        currentGameTicks,
                        debt,
                        pendingStatus,
                        confirmedSnapshot,
                        confirmedPayload));
                Log.Warning("[ClashOfRim] Bank debt repayment transaction failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                bankInProgress = false;
            }
        });
    }

    internal void StartHireMercenary(string skillDefName, int skillLevel, int durationDays)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            mercenaryStatus = atomicMessage;
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            mercenaryStatus = failureReason;
            return;
        }

        if (mercenaryInProgress || manualSyncInProgress || snapshotUploadInProgress)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            return;
        }

        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusNoMap");
            return;
        }

        mercenaryInProgress = true;
        manualSyncInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHiring");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"mercenary:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto> result =
                    await client.QuoteMercenaryAsync(currentTicks, skillDefName, skillLevel, durationDays);
                if (!result.Success || result.Response is null)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    lastBankStatus = result.Response.BankStatus;
                    return;
                }

                ModMercenaryQuoteResponseDto quote = result.Response;
                if (quote.PriceSilver <= 0)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusMissingContract");
                    return;
                }

                string contractId = "mercenary-" + Guid.NewGuid().ToString("N");
                var contract = new ModMercenaryContractDto
                {
                    ContractId = contractId,
                    SkillDefName = skillDefName,
                    SkillLevel = skillLevel,
                    DurationDays = durationDays,
                    PriceSilver = quote.PriceSilver,
                    HarmfulSurgeryFineSilver = quote.HarmfulSurgeryFineSilver,
                    DeathFineSilver = quote.DeathFineSilver,
                    CreatedAtGameTicks = currentTicks,
                    ExpiresAtGameTicks = currentTicks + durationDays * (long)ClashManagedQuestTimingUtility.TicksPerDay
                };
                lastBankStatus = quote.BankStatus ?? lastBankStatus;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    ApplyPreparedMercenaryHireAndBuildTransaction(idempotencyKey, currentTicks, contract));
            }
            catch (Exception ex)
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary hire failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
                mercenaryInProgress = false;
            }
        });
    }

    private void ApplyPreparedMercenaryHireAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHiring"));
        int availableSilver = CountPlayerHomeSilver();
        if (availableSilver < contract.PriceSilver)
        {
            ClearLocalAtomicMutation();
            mercenaryStatus = ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusInsufficientSilver",
                contract.PriceSilver.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryConsumePlayerHomeSilver(contract.PriceSilver, out string consumeMessage))
        {
            ClearLocalAtomicMutation();
            mercenaryStatus = consumeMessage;
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        Pawn pawn = MercenarySkillUtility.GenerateMercenaryPawn(
            contract.SkillDefName,
            contract.SkillLevel,
            contract.ContractId);
        ClashMercenaryQuestUtility.CreateMercenaryQuest(contract, pawn);
        if (!ClashMercenaryQuestUtility.TrySendArrivalShuttle(pawn, out string shuttleMessage))
        {
            mercenaryStatus = shuttleMessage;
            ShowUnconfirmedSnapshotFailure(
                operation,
                shuttleMessage,
                () => RetryPreparedMercenaryArrivalAndBuildTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract,
                    pawn));
            return;
        }

        mercenaryStatus = ClashOfRimText.Key(
            "ClashOfRim.Mercenary.StatusHired",
            pawn.LabelShort.Named("PAWN"),
            contract.PriceSilver.Named("PRICE"));
        Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
        BuildAndSubmitPreparedMercenaryHireTransaction(
            idempotencyKey,
            currentGameTicks,
            contract);
    }

    private void RetryPreparedMercenaryArrivalAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalPawnMissing");
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary"),
                mercenaryStatus,
                () => RetryPreparedMercenaryArrivalAndBuildTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract,
                    pawn));
            return;
        }

        if (!ClashMercenaryQuestUtility.TrySendArrivalShuttle(pawn, out string shuttleMessage))
        {
            mercenaryStatus = shuttleMessage;
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary"),
                shuttleMessage,
                () => RetryPreparedMercenaryArrivalAndBuildTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract,
                    pawn));
            return;
        }

        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalRetried");
        Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
        BuildAndSubmitPreparedMercenaryHireTransaction(
            idempotencyKey,
            currentGameTicks,
            contract);
    }

    private void BuildAndSubmitPreparedMercenaryHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanContinueManualSyncForLocalAtomicMutation(operation, out string failureReason))
        {
            mercenaryStatus = failureReason;
            ShowUnconfirmedSnapshotFailure(
                operation,
                failureReason,
                () => BuildAndSubmitPreparedMercenaryHireTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        manualSyncInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            mercenaryInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                mercenaryStatus,
                () => BuildAndSubmitPreparedMercenaryHireTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract));
            return;
        }

        SubmitPreparedMercenaryHireTransaction(
            idempotencyKey,
            currentGameTicks,
            contract,
            build.Package!,
            build.Payload!);
    }

    private void SubmitPreparedMercenaryHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto> result =
                    await client.HireMercenaryWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload);
                if (!result.Success || result.Response is null)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHireFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    mercenaryStatus = status;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => SubmitPreparedMercenaryHireTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            contract,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHireRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    mercenaryStatus = status;
                    if (result.Response.BankStatus is not null)
                    {
                        lastBankStatus = result.Response.BankStatus;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => SubmitPreparedMercenaryHireTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            contract,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    lastBankStatus = result.Response.BankStatus ?? lastBankStatus;
                    mercenaryStatus = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHiredConfirmed",
                        contract.PriceSilver.Named("PRICE"));
                    Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                mercenaryStatus = status;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    status,
                    () => SubmitPreparedMercenaryHireTransaction(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload));
                Log.Warning("[ClashOfRim] Mercenary hire transaction failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                mercenaryInProgress = false;
            }
        });
    }

    internal void StartQuoteMercenaryPrice(
        string requestKey,
        string skillDefName,
        int skillLevel,
        int durationDays,
        Action<string, string, ModMercenaryQuoteResponseDto?> callback)
    {
        if (callback is null)
        {
            return;
        }

        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        Task.Run(async () =>
        {
            string status;
            ModMercenaryQuoteResponseDto? quote = null;
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto> result =
                    await client.QuoteMercenaryAsync(currentTicks, skillDefName, skillLevel, durationDays);
                if (!result.Success || result.Response is null)
                {
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                }
                else if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    quote = result.Response;
                    lastBankStatus = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                }
                else
                {
                    quote = result.Response;
                    lastBankStatus = result.Response.BankStatus;
                    status = MercenaryUiUtility.FormatPriceLine(result.Response.PriceSilver);
                }
            }
            catch (Exception ex)
            {
                status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusQuoteException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary quote failed: " + ex);
            }

            ClashOfRimGameComponent.EnqueueMainThreadAction(() => callback(requestKey, status, quote));
        });
    }

    internal void StartQuoteMercenaryGuardPrice(
        string requestKey,
        string tier,
        Action<string, string, ModMercenaryGuardQuoteResponseDto?> callback)
    {
        if (callback is null)
        {
            return;
        }

        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        Task.Run(async () =>
        {
            string status;
            ModMercenaryGuardQuoteResponseDto? quote = null;
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto> result =
                    await client.QuoteMercenaryGuardAsync(currentTicks, tier);
                if (!result.Success || result.Response is null)
                {
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                }
                else if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    quote = result.Response;
                    lastBankStatus = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                }
                else
                {
                    quote = result.Response;
                    lastBankStatus = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.GuardPriceLine",
                        result.Response.PriceSilver.Named("PRICE"));
                }
            }
            catch (Exception ex)
            {
                status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusQuoteException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary guard quote failed: " + ex);
            }

            ClashOfRimGameComponent.EnqueueMainThreadAction(() => callback(requestKey, status, quote));
        });
    }

    internal void StartHireMercenaryGuard(string tier)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            mercenaryStatus = atomicMessage;
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            mercenaryStatus = failureReason;
            return;
        }

        if (mercenaryInProgress || manualSyncInProgress || snapshotUploadInProgress)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            return;
        }

        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome) ?? Find.CurrentMap;
        if (map is null)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusNoMap");
            return;
        }

        mercenaryInProgress = true;
        manualSyncInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.GuardStatusHiring");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"mercenary-guard:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto> result =
                    await client.QuoteMercenaryGuardAsync(currentTicks, tier);
                if (!result.Success || result.Response is null)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    lastBankStatus = result.Response.BankStatus;
                    return;
                }

                ModMercenaryGuardQuoteResponseDto quote = result.Response;
                if (quote.PriceSilver <= 0 || quote.PointRatio <= 0f)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusMissingContract");
                    return;
                }

                var contract = new ModMercenaryGuardContractDto
                {
                    ContractId = "mercenary-guard-" + Guid.NewGuid().ToString("N"),
                    Tier = quote.Tier,
                    PriceSilver = quote.PriceSilver,
                    PointRatio = quote.PointRatio,
                    CreatedAtGameTicks = currentTicks
                };
                lastBankStatus = quote.BankStatus ?? lastBankStatus;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    ApplyPreparedMercenaryGuardHireAndBuildTransaction(idempotencyKey, currentTicks, contract));
            }
            catch (Exception ex)
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary guard hire failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
                mercenaryInProgress = false;
            }
        });
    }

    private void ApplyPreparedMercenaryGuardHireAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.GuardStatusHiring"));
        int availableSilver = CountPlayerHomeSilver();
        if (availableSilver < contract.PriceSilver)
        {
            ClearLocalAtomicMutation();
            mercenaryStatus = ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusInsufficientSilver",
                contract.PriceSilver.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryConsumePlayerHomeSilver(contract.PriceSilver, out string consumeMessage))
        {
            ClearLocalAtomicMutation();
            mercenaryStatus = consumeMessage;
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        mercenaryStatus = ClashOfRimText.Key(
            "ClashOfRim.Mercenary.GuardStatusPurchased",
            contract.PriceSilver.Named("PRICE"));
        Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
        BuildAndSubmitPreparedMercenaryGuardHireTransaction(
            idempotencyKey,
            currentGameTicks,
            contract);
    }

    private void BuildAndSubmitPreparedMercenaryGuardHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanContinueManualSyncForLocalAtomicMutation(operation, out string failureReason))
        {
            mercenaryStatus = failureReason;
            ShowUnconfirmedSnapshotFailure(
                operation,
                failureReason,
                () => BuildAndSubmitPreparedMercenaryGuardHireTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract));
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        manualSyncInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
            manualSyncInProgress = false;
            mercenaryInProgress = false;
            ShowUnconfirmedSnapshotFailure(
                operation,
                mercenaryStatus,
                () => BuildAndSubmitPreparedMercenaryGuardHireTransaction(
                    idempotencyKey,
                    currentGameTicks,
                    contract));
            return;
        }

        SubmitPreparedMercenaryGuardHireTransaction(
            idempotencyKey,
            currentGameTicks,
            contract,
            build.Package!,
            build.Payload!);
    }

    private void SubmitPreparedMercenaryGuardHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(allowExistingManualSync: true))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryGuardHireResponseDto> result =
                    await client.HireMercenaryGuardWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload);
                if (!result.Success || result.Response is null)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHireFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    mercenaryStatus = status;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => SubmitPreparedMercenaryGuardHireTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            contract,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    string status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHireRejected",
                        response.ErrorCode.Named("CODE"),
                        (response.Message ?? string.Empty).Named("MESSAGE"));
                    mercenaryStatus = status;
                    if (result.Response.BankStatus is not null)
                    {
                        lastBankStatus = result.Response.BankStatus;
                    }

                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        status,
                        () => SubmitPreparedMercenaryGuardHireTransaction(
                            idempotencyKey,
                            currentGameTicks,
                            contract,
                            confirmedSnapshot,
                            confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    lastBankStatus = result.Response.BankStatus ?? lastBankStatus;
                    mercenaryStatus = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.GuardStatusConfirmed",
                        contract.PriceSilver.Named("PRICE"));
                    Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                string status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                mercenaryStatus = status;
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    status,
                    () => SubmitPreparedMercenaryGuardHireTransaction(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload));
                Log.Warning("[ClashOfRim] Mercenary guard hire transaction failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
                mercenaryInProgress = false;
            }
        });
    }

    internal void StartReportMercenaryIncident(string contractId, string incidentKind, string? idempotencyKeyOverride = null)
    {
        if (string.IsNullOrWhiteSpace(contractId) || string.IsNullOrWhiteSpace(incidentKind))
        {
            return;
        }

        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusNotConfigured");
            return;
        }

        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusReportingIncident");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKeyOverride)
            ? $"mercenary-incident:{settings.UserId}:{settings.ColonyId}:{contractId}:{incidentKind}:{DateTime.UtcNow.Ticks}"
            : idempotencyKeyOverride!;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto> result =
                    await client.ReportMercenaryIncidentAsync(idempotencyKey, currentTicks, contractId, incidentKind);
                if (!result.Success || result.Response is null)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    lastBankStatus = result.Response.BankStatus;
                    return;
                }

                lastBankStatus = result.Response.BankStatus;
                mercenaryStatus = result.Response.Debt is null
                    ? ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentReportedNoDebt")
                    : ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentReported", result.Response.Debt.AmountSilver.Named("FINE"));
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (result.Response.Debt is not null && result.Response.BankStatus is not null)
                    {
                        ClashBankLoanQuestUtility.CreateOrUpdateDebtQuest(result.Response.Debt, result.Response.BankStatus);
                    }

                    Messages.Message(mercenaryStatus, MessageTypeDefOf.NegativeEvent, historical: false);
                    StartMercenarySnapshotConfirmation();
                });
            }
            catch (Exception ex)
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary incident report failed: " + ex);
            }
        });
    }

    private static ModBankLoanSummaryDto CloneLoanWithLocalTotalDue(ModBankLoanSummaryDto loan)
    {
        int totalDue = Math.Max(
            loan.TotalDueSilver,
            ClashBankLoanQuestUtility.FindLoanTotalDueSilver(loan.LoanId) ?? loan.TotalDueSilver);
        return new ModBankLoanSummaryDto
        {
            LoanId = loan.LoanId,
            PrincipalSilver = loan.PrincipalSilver,
            InterestSilver = loan.InterestSilver,
            TotalDueSilver = totalDue,
            DurationDays = loan.DurationDays,
            CreatedAtGameTicks = loan.CreatedAtGameTicks,
            DueAtGameTicks = loan.DueAtGameTicks,
            PenaltyRaidPoints = loan.PenaltyRaidPoints,
            Status = loan.Status,
            SourceKind = loan.SourceKind
        };
    }
}
