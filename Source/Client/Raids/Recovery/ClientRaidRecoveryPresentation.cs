using System;
using AIRsLight.ClashOfRim.Modules.Raids;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidRecoveryPresentation : IClientRaidRecoveryPresentation
{
    private readonly Action<string> status;
    internal ClientRaidRecoveryPresentation(Action<string> status) => this.status = status;
    public string Format(ClientRaidRecoveryNotice notice) => notice.State switch
    {
        ClientRaidRecoveryState.Waiting => ClashOfRimText.Key("ClashOfRim.ServerEntry.StageLoad"),
        ClientRaidRecoveryState.ServerSettled => ClashOfRimText.Key("ClashOfRim.Raid.RestoreServerAlreadySettled"),
        ClientRaidRecoveryState.ServerUnresolved => ClashOfRimText.Key("ClashOfRim.Raid.RestoreServerUnresolved", notice.EventId.Named("EVENTID")),
        ClientRaidRecoveryState.MissingBattle => ClashOfRimText.Key("ClashOfRim.Raid.RestoreMissingLocalBattle", notice.EventId.Named("EVENTID")),
        ClientRaidRecoveryState.InvalidTime => ClashOfRimText.Key("ClashOfRim.Raid.RestoreInvalidServerTime", notice.EventId.Named("EVENTID")),
        ClientRaidRecoveryState.Restored => ClashOfRimText.Key("ClashOfRim.Raid.RestoreSucceeded", notice.RemainingSeconds.Named("SECONDS")),
        ClientRaidRecoveryState.Exception => ClashOfRimText.Key("ClashOfRim.Raid.RestoreException", (notice.Exception?.GetType().Name ?? "").Named("TYPE"), (notice.Exception?.Message ?? "").Named("MESSAGE")),
        _ => throw new ArgumentOutOfRangeException(nameof(notice.State))
    };
    public void Report(ClientRaidRecoveryNotice notice, string message)
    {
        if (notice.State is ClientRaidRecoveryState.Restored or ClientRaidRecoveryState.ServerSettled)
            Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
        else if (notice.State != ClientRaidRecoveryState.Waiting) status(message);
        if (notice.State is ClientRaidRecoveryState.Waiting or ClientRaidRecoveryState.ServerUnresolved or ClientRaidRecoveryState.MissingBattle or ClientRaidRecoveryState.InvalidTime or ClientRaidRecoveryState.Exception)
            Log.Warning("[ClashOfRim][Raid] recovery=" + notice.State + " event=" + notice.EventId + " message=" + message + " exception=" + notice.Exception);
    }
}
