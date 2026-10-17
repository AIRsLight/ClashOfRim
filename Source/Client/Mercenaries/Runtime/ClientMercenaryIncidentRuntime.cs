using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal sealed class ClientMercenaryIncidentRuntime : IClientMercenaryIncidentRuntime
{
    public long GameTicks => Find.TickManager?.TicksGame ?? 0;
    public void ApplyDebt(ModBankDebtSummaryDto debt, ModBankStatusResponseDto status) =>
        ClashBankLoanQuestUtility.CreateOrUpdateDebtQuest(debt, status);
}
