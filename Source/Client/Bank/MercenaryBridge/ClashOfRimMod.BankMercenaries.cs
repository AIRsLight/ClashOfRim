using AIRsLight.ClashOfRim.Modules.Synchronization;
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
    internal void StartRefreshBankStatus() => _ = bankOperations.RefreshAsync();
    internal void StartCreateBankLoan(int principalSilver, int durationDays) => _ = bankOperations.BorrowAsync(principalSilver, durationDays);
    internal void StartRepayBankLoan() => _ = bankOperations.RepayLoanAsync();
    internal void StartRepayBankDebt(ModBankDebtSummaryDto debt) => _ = bankOperations.RepayDebtAsync(debt?.DebtId);

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

        if (mercenaryInProgress)
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

        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason))
        {
            mercenaryStatus = failureReason;
            return;
        }
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHiring");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"mercenary:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto> result =
                    await ClientSessionWork.Observe(client.QuoteMercenaryAsync(currentTicks, skillDefName, skillLevel, durationDays));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    bankState.View = result.Response.BankStatus;
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
                bankState.View = quote.BankStatus ?? bankState.View;
                manualOperation.QueueContinuation(EnqueueClashOfRimMainThreadAction,
                    continuation => ApplyPreparedMercenaryHireAndBuildTransaction(idempotencyKey, currentTicks, contract, continuation),
                    () => mercenaryInProgress = false);
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary hire failed: " + ex);
            }
            finally
            {
                manualOperation.Complete(() => mercenaryInProgress = false);
            }
        });
    }

    private void ApplyPreparedMercenaryHireAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ClientSynchronizationCoordinator.Lease continuation)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHiring"));
        int availableSilver = ClientBankRuntime.CountPlayerHomeSilver();
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

        if (!ClientBankRuntime.TryConsumePlayerHomeSilver(contract.PriceSilver, out string consumeMessage))
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
            contract,
            continuation);
    }

    private void RetryPreparedMercenaryArrivalAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        Pawn? pawn)
    {
        if (!TryBeginManualSynchronization(out var manualOperation, out string failureReason))
        {
            mercenaryStatus = failureReason;
            return;
        }
        try
        {
            if (pawn is null || pawn.Destroyed)
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalPawnMissing");
                ShowUnconfirmedSnapshotFailure(
                    LocalMutationKind.Mercenary,
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
                    LocalMutationKind.Mercenary,
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
                contract,
                manualOperation);
        }
        finally { manualOperation.Complete(() => mercenaryInProgress = false); }
    }

    private void BuildAndSubmitPreparedMercenaryHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason, operation))
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

        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason, continuation))
        {
            mercenaryStatus = failureReason;
            return;
        }

        try
        {
            BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
            mercenaryInProgress = true;
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
            if (!snapshotPreparation.TryBuild(
                    settings.UserId,
                    settings.ColonyId,
                    out ModSnapshotPackageBuildResult build,
                    out string buildFailureReason))
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
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
                build.Payload!,
                manualOperation);
        }
        finally { manualOperation.Complete(() => mercenaryInProgress = false); }
    }

    private void SubmitPreparedMercenaryHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto> result =
                    await ClientSessionWork.Observe(client.HireMercenaryWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload));
                using var publication = ClientSessionWork.EnterPublication();
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
                        bankState.View = result.Response.BankStatus;
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
                        snapshotPublication.PublishCurrentGame(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    bankState.View = result.Response.BankStatus ?? bankState.View;
                    mercenaryStatus = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusHiredConfirmed",
                        contract.PriceSilver.Named("PRICE"));
                    Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
                uploadTransaction.Complete(() => mercenaryInProgress = false);
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
        ClientSessionWork.Run(async () =>
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
                    await ClientSessionWork.Observe(client.QuoteMercenaryAsync(currentTicks, skillDefName, skillLevel, durationDays));
                using var publication = ClientSessionWork.EnterPublication();
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
                    bankState.View = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                }
                else
                {
                    quote = result.Response;
                    bankState.View = result.Response.BankStatus;
                    status = MercenaryUiUtility.FormatPriceLine(result.Response.PriceSilver);
                }
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusQuoteException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary quote failed: " + ex);
            }

            using (ClientSessionWork.EnterPublication())
            {
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => callback(requestKey, status, quote));
            }
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
        ClientSessionWork.Run(async () =>
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
                    await ClientSessionWork.Observe(client.QuoteMercenaryGuardAsync(currentTicks, tier));
                using var publication = ClientSessionWork.EnterPublication();
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
                    bankState.View = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.StatusQuoteRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                }
                else
                {
                    quote = result.Response;
                    bankState.View = result.Response.BankStatus;
                    status = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.GuardPriceLine",
                        result.Response.PriceSilver.Named("PRICE"));
                }
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                status = ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.StatusQuoteException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary guard quote failed: " + ex);
            }

            using (ClientSessionWork.EnterPublication())
            {
                ClashOfRimGameComponent.EnqueueMainThreadAction(() => callback(requestKey, status, quote));
            }
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

        if (mercenaryInProgress)
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

        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason))
        {
            mercenaryStatus = failureReason;
            return;
        }
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.GuardStatusHiring");
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        string idempotencyKey = $"mercenary-guard:{settings.UserId}:{settings.ColonyId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto> result =
                    await ClientSessionWork.Observe(client.QuoteMercenaryGuardAsync(currentTicks, tier));
                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusHireRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    bankState.View = result.Response.BankStatus;
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
                bankState.View = quote.BankStatus ?? bankState.View;
                manualOperation.QueueContinuation(EnqueueClashOfRimMainThreadAction,
                    continuation => ApplyPreparedMercenaryGuardHireAndBuildTransaction(idempotencyKey, currentTicks, contract, continuation),
                    () => mercenaryInProgress = false);
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary guard hire failed: " + ex);
            }
            finally
            {
                manualOperation.Complete(() => mercenaryInProgress = false);
            }
        });
    }

    private void ApplyPreparedMercenaryGuardHireAndBuildTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ClientSynchronizationCoordinator.Lease continuation)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.GuardStatusHiring"));
        int availableSilver = ClientBankRuntime.CountPlayerHomeSilver();
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

        if (!ClientBankRuntime.TryConsumePlayerHomeSilver(contract.PriceSilver, out string consumeMessage))
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
            contract,
            continuation);
    }

    private void BuildAndSubmitPreparedMercenaryGuardHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason, operation))
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

        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason, continuation))
        {
            mercenaryStatus = failureReason;
            return;
        }

        try
        {
            BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
            mercenaryInProgress = true;
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
            if (!snapshotPreparation.TryBuild(
                    settings.UserId,
                    settings.ColonyId,
                    out ModSnapshotPackageBuildResult build,
                    out string buildFailureReason))
            {
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusConfirmationFailed", "BuildSnapshotFailed".Named("CODE"), buildFailureReason.Named("MESSAGE"));
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
                build.Payload!,
                manualOperation);
        }
        finally { manualOperation.Complete(() => mercenaryInProgress = false); }
    }

    private void SubmitPreparedMercenaryGuardHireTransaction(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Mercenary;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            mercenaryStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusBusy");
            Messages.Message(mercenaryStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"));
        mercenaryInProgress = true;
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation");
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModMercenaryGuardHireResponseDto> result =
                    await ClientSessionWork.Observe(client.HireMercenaryGuardWithSnapshotAsync(
                        idempotencyKey,
                        currentGameTicks,
                        contract,
                        confirmedSnapshot,
                        confirmedPayload));
                using var publication = ClientSessionWork.EnterPublication();
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
                        bankState.View = result.Response.BankStatus;
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
                        snapshotPublication.PublishCurrentGame(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    bankState.View = result.Response.BankStatus ?? bankState.View;
                    mercenaryStatus = ClashOfRimText.Key(
                        "ClashOfRim.Mercenary.GuardStatusConfirmed",
                        contract.PriceSilver.Named("PRICE"));
                    Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
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
                uploadTransaction.Complete(() => mercenaryInProgress = false);
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
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusIncidentRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    bankState.View = result.Response.BankStatus;
                    return;
                }

                bankState.View = result.Response.BankStatus;
                mercenaryStatus = result.Response.Debt is null
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

                    Messages.Message(mercenaryStatus, MessageTypeDefOf.NegativeEvent, historical: false);
                    StartMercenarySnapshotConfirmation();
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Mercenary incident report failed: " + ex);
            }
        });
    }


}
