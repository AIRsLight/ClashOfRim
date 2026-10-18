using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal static class ClientRaidPreparationPresentation
{
    internal static string Failure(ClientRaidPreparationFailure failure) => failure.Kind switch
    {
        ClientRaidPreparationFailureKind.Transport => ClashOfRimText.Key("ClashOfRim.Raid.StatusCreateFailed",
            (failure.Code ?? string.Empty).Named("CODE"), (failure.Message ?? string.Empty).Named("MESSAGE")),
        ClientRaidPreparationFailureKind.Rejected => ClashOfRimText.Key("ClashOfRim.Raid.StatusCreateRejected",
            failure.Code.Named("CODE"), (failure.Message ?? string.Empty).Named("MESSAGE")),
        ClientRaidPreparationFailureKind.MissingPreparation => ClashOfRimText.Key("ClashOfRim.Raid.StatusCreateRejected",
            "MissingPreparation".Named("CODE"), ClashOfRimText.Key("ClashOfRim.Raid.StatusMissingPreparation").Named("MESSAGE")),
        _ => RemoteSnapshotDownloadValidator.FormatFailure(failure.Download!, new("ClashOfRim.Raid.StatusBattleDownloadFailed",
            "ClashOfRim.Raid.StatusBattleMissingSnapshot", "ClashOfRim.Raid.StatusBattlePayloadFailed"))
    };
}
