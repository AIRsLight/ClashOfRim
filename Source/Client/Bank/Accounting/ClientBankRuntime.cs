using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Bank;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal sealed class ClientBankRuntime : IClientBankRuntime
{
    public ClientBankFacts ReadFacts() => new(Find.TickManager?.TicksGame ?? 0, CalculateCurrentColonyWealth(),
        CountPlayerHomeSilver(), LoanMap() is not null);
    private static Map? LoanMap() => Find.Maps?.FirstOrDefault(map => map.IsPlayerHome) ?? Find.CurrentMap;
    public int LoanTotalDue(string loanId, int serverTotal) => Math.Max(serverTotal, ClashBankLoanQuestUtility.FindLoanTotalDueSilver(loanId) ?? serverTotal);
    public ClientBankApplication Apply(ClientBankCommand command, ModBankStatusResponseDto pending)
    {
        if (command.Operation == ClientBankOperation.Borrow)
        {
            if (!TryDropBankLoanSilverOnPlayerMap(command.Loan!, command.IdempotencyKey, LoanMap()!, out var message)) return new(false, message);
            ClashBankLoanQuestUtility.CreateOrUpdateLoanQuest(command.Loan!, pending);
        }
        else
        {
            if (!TryConsumePlayerHomeSilver(command.Silver, out var message)) return new(false, message);
            if (command.Operation == ClientBankOperation.RepayLoan) ClashBankLoanQuestUtility.MarkLoanRepaid(command.Loan!.LoanId);
            else ClashBankLoanQuestUtility.MarkDebtRepaid(command.Debt!.DebtId);
        }
        return new(true, string.Empty);
    }
    public void ConfirmLoan(ModBankLoanSummaryDto loan, ModBankStatusResponseDto status) => ClashBankLoanQuestUtility.CreateOrUpdateLoanQuest(loan, status);
    public void RefreshDebts(ModBankStatusResponseDto status) => ClashBankLoanQuestUtility.CreateOrUpdateDebtQuests(status);

    internal static int CalculateCurrentColonyWealth()
    {
        return (int)Math.Max(0f, Find.Maps?
            .Where(map => map.IsPlayerHome)
            .Sum(map => map.wealthWatcher?.WealthTotal ?? 0f) ?? 0f);
    }

    internal static int CountPlayerHomeSilver()
    {
        return PlayerHomeSilverThings().Sum(thing => Math.Max(0, thing.stackCount));
    }

    internal static bool TryConsumePlayerHomeSilver(int amount, out string message)
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

    internal static bool TryDropBankLoanSilverOnPlayerMap(
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

    internal static IEnumerable<Thing> PlayerHomeSilverThings()
    {
        return Find.Maps?
            .Where(map => map.IsPlayerHome)
            .SelectMany(map => map.listerThings?.ThingsOfDef(ThingDefOf.Silver) ?? Enumerable.Empty<Thing>())
            .Where(thing => thing is not null && !thing.Destroyed)
            ?? Enumerable.Empty<Thing>();
    }
}
