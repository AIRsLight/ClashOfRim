using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Mercenaries;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private void StartBankSnapshotConfirmation()
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationBank"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Bank.StatusUploadingConfirmation"),
            RetryAction = StartBankSnapshotConfirmation,
            SetStatus = value => bankStatus = value,
            BuildFailureStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusConfirmationFailed",
                (result.ErrorCode ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")),
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusConfirmationSucceeded",
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnSuccessOnMainThread = _ =>
            {
                StartRefreshBankStatus();
            }
        });
    }

    private void RetryMercenaryArrivalAndConfirmation(Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalPawnMissing");
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary"),
                mercenaryStatus,
                () => RetryMercenaryArrivalAndConfirmation(pawn));
            return;
        }

        if (!ClashMercenaryQuestUtility.TrySendArrivalShuttle(pawn, out string shuttleMessage))
        {
            mercenaryStatus = shuttleMessage;
            ShowUnconfirmedSnapshotFailure(
                ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary"),
                shuttleMessage,
                () => RetryMercenaryArrivalAndConfirmation(pawn));
            return;
        }

        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusArrivalRetried");
        Messages.Message(mercenaryStatus, MessageTypeDefOf.PositiveEvent, historical: false);
        StartMercenarySnapshotConfirmation();
    }

    internal void StartMercenarySnapshotConfirmation()
    {
        StartConfirmLocalMutationSnapshot(new LocalMutationSnapshotConfirmationRequest
        {
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationMercenary"),
            UploadingStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.StatusUploadingConfirmation"),
            RetryAction = StartMercenarySnapshotConfirmation,
            SetStatus = value => mercenaryStatus = value,
            BuildFailureStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusConfirmationFailed",
                (result.ErrorCode ?? string.Empty).Named("CODE"),
                (result.Message ?? string.Empty).Named("MESSAGE")),
            BuildSuccessStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusConfirmationSucceeded",
                (result.AcceptedSnapshotId ?? settings.CurrentSnapshotId).Named("SNAPSHOT")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Mercenary.StatusException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnSuccessOnMainThread = _ =>
            {
                StartRefreshBankStatus();
            }
        });
    }

    private static int CalculateCurrentColonyWealth()
    {
        return (int)Math.Max(0f, Find.Maps?
            .Where(map => map.IsPlayerHome)
            .Sum(map => map.wealthWatcher?.WealthTotal ?? 0f) ?? 0f);
    }

    private static ModBankStatusResponseDto CloneBankStatusWithActiveLoan(
        ModBankStatusResponseDto source,
        ModBankLoanSummaryDto activeLoan)
    {
        return new ModBankStatusResponseDto
        {
            Result = source.Result,
            ColonyWealth = source.ColonyWealth,
            MinLoanSilver = source.MinLoanSilver,
            ConfiguredMaxLoanSilver = source.ConfiguredMaxLoanSilver,
            MaxLoanSilver = source.MaxLoanSilver,
            MaxLoanWealthRatio = source.MaxLoanWealthRatio,
            BaseAnnualInterestRate = source.BaseAnnualInterestRate,
            MinDurationDays = source.MinDurationDays,
            MaxDurationDays = source.MaxDurationDays,
            BankLoansEnabled = source.BankLoansEnabled,
            MercenariesEnabled = source.MercenariesEnabled,
            MercenaryMinDurationDays = source.MercenaryMinDurationDays,
            MercenaryMaxDurationDays = source.MercenaryMaxDurationDays,
            InterestDurationMultiplierCurve = source.InterestDurationMultiplierCurve?.ToList() ?? new List<ModBankInterestDurationMultiplierPointDto>(),
            PenaltyIntervalDays = source.PenaltyIntervalDays,
            PenaltyRaidPointsPerSilver = source.PenaltyRaidPointsPerSilver,
            OverduePenaltyStages = source.OverduePenaltyStages?.ToList() ?? new List<ModBankOverduePenaltyStageDto>(),
            ActiveLoan = activeLoan,
            OpenDebts = source.OpenDebts?.ToList() ?? new List<ModBankDebtSummaryDto>(),
            TotalOpenDebtSilver = source.TotalOpenDebtSilver
        };
    }

    private static ModBankStatusResponseDto CloneBankStatusWithoutActiveLoan(ModBankStatusResponseDto? source)
    {
        if (source is null)
        {
            return new ModBankStatusResponseDto();
        }

        return new ModBankStatusResponseDto
        {
            Result = source.Result,
            ColonyWealth = source.ColonyWealth,
            MinLoanSilver = source.MinLoanSilver,
            ConfiguredMaxLoanSilver = source.ConfiguredMaxLoanSilver,
            MaxLoanSilver = source.MaxLoanSilver,
            MaxLoanWealthRatio = source.MaxLoanWealthRatio,
            BaseAnnualInterestRate = source.BaseAnnualInterestRate,
            MinDurationDays = source.MinDurationDays,
            MaxDurationDays = source.MaxDurationDays,
            BankLoansEnabled = source.BankLoansEnabled,
            MercenariesEnabled = source.MercenariesEnabled,
            MercenaryMinDurationDays = source.MercenaryMinDurationDays,
            MercenaryMaxDurationDays = source.MercenaryMaxDurationDays,
            InterestDurationMultiplierCurve = source.InterestDurationMultiplierCurve?.ToList() ?? new List<ModBankInterestDurationMultiplierPointDto>(),
            PenaltyIntervalDays = source.PenaltyIntervalDays,
            PenaltyRaidPointsPerSilver = source.PenaltyRaidPointsPerSilver,
            OverduePenaltyStages = source.OverduePenaltyStages?.ToList() ?? new List<ModBankOverduePenaltyStageDto>(),
            ActiveLoan = null,
            OpenDebts = source.OpenDebts?.ToList() ?? new List<ModBankDebtSummaryDto>(),
            TotalOpenDebtSilver = source.TotalOpenDebtSilver
        };
    }

    private static ModBankStatusResponseDto CloneBankStatusWithoutDebt(ModBankStatusResponseDto? source, string debtId)
    {
        if (source is null)
        {
            return new ModBankStatusResponseDto();
        }

        List<ModBankDebtSummaryDto> openDebts = source.OpenDebts?
            .Where(debt => !string.Equals(debt.DebtId, debtId, StringComparison.Ordinal))
            .ToList()
            ?? new List<ModBankDebtSummaryDto>();
        return new ModBankStatusResponseDto
        {
            Result = source.Result,
            ColonyWealth = source.ColonyWealth,
            MinLoanSilver = source.MinLoanSilver,
            ConfiguredMaxLoanSilver = source.ConfiguredMaxLoanSilver,
            MaxLoanSilver = source.MaxLoanSilver,
            MaxLoanWealthRatio = source.MaxLoanWealthRatio,
            BaseAnnualInterestRate = source.BaseAnnualInterestRate,
            MinDurationDays = source.MinDurationDays,
            MaxDurationDays = source.MaxDurationDays,
            BankLoansEnabled = source.BankLoansEnabled,
            MercenariesEnabled = source.MercenariesEnabled,
            MercenaryMinDurationDays = source.MercenaryMinDurationDays,
            MercenaryMaxDurationDays = source.MercenaryMaxDurationDays,
            InterestDurationMultiplierCurve = source.InterestDurationMultiplierCurve?.ToList() ?? new List<ModBankInterestDurationMultiplierPointDto>(),
            PenaltyIntervalDays = source.PenaltyIntervalDays,
            PenaltyRaidPointsPerSilver = source.PenaltyRaidPointsPerSilver,
            OverduePenaltyStages = source.OverduePenaltyStages?.ToList() ?? new List<ModBankOverduePenaltyStageDto>(),
            ActiveLoan = source.ActiveLoan,
            OpenDebts = openDebts,
            TotalOpenDebtSilver = openDebts.Sum(debt => Math.Max(0, debt.AmountSilver))
        };
    }

    private static int CountPlayerHomeSilver()
    {
        return PlayerHomeSilverThings().Sum(thing => Math.Max(0, thing.stackCount));
    }

    private static bool TryConsumePlayerHomeSilver(int amount, out string message)
    {
        int remaining = Math.Max(0, amount);
        foreach (Thing silver in PlayerHomeSilverThings().OrderBy(thing => thing.stackCount).ToList())
        {
            if (remaining <= 0)
            {
                break;
            }

            int take = Math.Min(remaining, Math.Max(0, silver.stackCount));
            if (take <= 0)
            {
                continue;
            }

            Thing removed = silver.SplitOff(take);
            if (!removed.Destroyed)
            {
                removed.Destroy(DestroyMode.Vanish);
            }

            remaining -= take;
        }

        if (remaining > 0)
        {
            message = ClashOfRimText.Key("ClashOfRim.Bank.StatusConsumeSilverFailed", remaining.Named("REMAINING"));
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryDropBankLoanSilverOnPlayerMap(
        ModBankLoanSummaryDto loan,
        string idempotencyKey,
        Map map,
        out string message)
    {
        if (map is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Bank.StatusNoMap");
            return false;
        }

        int amount = Math.Max(1, loan.PrincipalSilver);
        var plan = new GiftLandingPlan(
            idempotencyKey + ":funds",
            worldObjectId: null,
            targetMapUniqueId: "Map_" + map.uniqueID,
            tile: null,
            landingMode: "DropPod",
            new[]
            {
                new GiftItemReference(
                    "bank-loan:" + loan.LoanId + ":" + idempotencyKey,
                    ThingDefOf.Silver.defName,
                    amount,
                    sourceSnapshotId: null)
            },
            requiresSnapshotConfirmation: true,
            arrivalLetterLabel: ClashOfRimText.Key("ClashOfRim.Bank.LoanArrivalLetterLabel"),
            arrivalLetterText: ClashOfRimText.Key(
                "ClashOfRim.Bank.LoanArrivalLetterText",
                amount.Named("PRINCIPAL")));
        GiftLandingApplicationResult result = GiftLandingApplicator.ApplyToMap(plan, map);
        if (result.Success)
        {
            message = string.Empty;
            return true;
        }

        message = string.IsNullOrWhiteSpace(result.Message)
            ? ClashOfRimText.Key("ClashOfRim.Bank.StatusPlaceSilverFailed")
            : result.Message;
        return false;
    }

    private static IEnumerable<Thing> PlayerHomeSilverThings()
    {
        return Find.Maps?
            .Where(map => map.IsPlayerHome)
            .SelectMany(map => map.listerThings?.ThingsOfDef(ThingDefOf.Silver) ?? Enumerable.Empty<Thing>())
            .Where(thing => thing is not null && !thing.Destroyed)
            ?? Enumerable.Empty<Thing>();
    }


    private void SetBankStatus(string status, MessageTypeDef? messageDef = null)
    {
        bankStatus = status;
        if (messageDef is null)
        {
            return;
        }

        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
            Messages.Message(status, messageDef, historical: false));
    }

}

