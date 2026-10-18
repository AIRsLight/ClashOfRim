using AIRsLight.ClashOfRim.Modules.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal static class ClientRaidSupportLossPresentation
{
    internal static string Failure(ClientRaidSupportLossFailure failure) => failure.Kind switch
    {
        ClientRaidSupportLossFailureKind.Transport or ClientRaidSupportLossFailureKind.Rejected => ClashOfRimText.Key(
            failure.Kind == ClientRaidSupportLossFailureKind.Transport ? "ClashOfRim.Raid.StatusSettlementSupportLossFailed" : "ClashOfRim.Raid.StatusSettlementSupportLossRejected",
            (failure.PawnName ?? string.Empty).Named("PAWN"), (failure.Code ?? string.Empty).Named("CODE"), (failure.Message ?? string.Empty).Named("MESSAGE")),
        ClientRaidSupportLossFailureKind.LocalRemoval => ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementSupportLossLocalRemoveFailed", (failure.PawnName ?? string.Empty).Named("PAWN")),
        ClientRaidSupportLossFailureKind.Incomplete => ClashOfRimText.Key("ClashOfRim.Support.StatusReturnConfirmationIncomplete"),
        _ => ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementSupportLossException",
            (failure.Exception?.GetType().Name ?? string.Empty).Named("TYPE"), (failure.Exception?.Message ?? string.Empty).Named("MESSAGE"))
    };
}
