using AIRsLight.ClashOfRim.Modules.Events;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

internal static class ClientEventReadPresentation
{
    internal static string Started(ClientEventReadKind kind) => ClashOfRimText.Key("ClashOfRim.Events." + (kind switch
    {
        ClientEventReadKind.Queue => "StatusPullingPending",
        ClientEventReadKind.Wait => "StatusWaitingOnline",
        ClientEventReadKind.Details => "StatusPullingDetails",
        _ => "StatusRefreshing"
    }));

    internal static string Empty(ClientEventReadKind kind) => ClashOfRimText.Key("ClashOfRim.Events." + (kind switch
    {
        ClientEventReadKind.Automatic => "StatusAutoNoEvents",
        ClientEventReadKind.Details => "StatusNoViewableDetails",
        _ => "StatusNoDetails"
    }));

    internal static string Failed(ClientEventReadUpdate update)
    {
        var failure = update.Failure!;
        bool automatic = update.Request.Kind == ClientEventReadKind.Automatic;
        if (failure.Kind == ClientEventReadFailureKind.Exception)
        {
            string key = update.Request.Kind switch
            {
                ClientEventReadKind.Automatic => "StatusAutoException",
                ClientEventReadKind.Queue => "StatusQueueException",
                ClientEventReadKind.Wait => "StatusOnlineException",
                ClientEventReadKind.Details => "StatusDetailsException",
                _ => "StatusException"
            };
            return ClashOfRimText.Key("ClashOfRim.Events." + key,
                (failure.Code ?? string.Empty).Named("TYPE"), (failure.Message ?? string.Empty).Named("MESSAGE"));
        }
        string prefix = update.Stage == ClientEventReadStage.Details ? (automatic ? "StatusAutoDetails" : "StatusDetails")
            : update.Request.Kind == ClientEventReadKind.Wait ? "StatusOnlineWait" : automatic ? "StatusAutoQueue" : "StatusQueue";
        return ClashOfRimText.Key("ClashOfRim.Events." + prefix + (failure.Kind == ClientEventReadFailureKind.Rejected ? "Rejected" : "Failed"),
            (failure.Code ?? string.Empty).Named("CODE"), (failure.Message ?? string.Empty).Named("MESSAGE"));
    }
}
