using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal sealed class ClientObservationPresentation : IClientObservationPresentation
{
    private readonly Action<string> status;
    private readonly Action<ModLoginResponseDto?> compatibilityMismatch;
    internal ClientObservationPresentation(Action<string> status, Action<ModLoginResponseDto?> compatibilityMismatch)
    { this.status = status; this.compatibilityMismatch = compatibilityMismatch; }
    public void Reject(string key) => Failure(ClashOfRimText.Key(key), false);
    public void Downloading(string mode, ModWorldMapMarkerDto target) => Progress(ClashOfRimText.Key(
        ClientRemoteMapModes.DownloadingKey(mode), ClientWorldTargetPresentation.Label(target).Named("TARGET")));
    public void Generating(ModWorldMapMarkerDto target) => Progress(ClashOfRimText.Key(
        "ClashOfRim.RemoteSessionMap.StatusGenerating", ClientWorldTargetPresentation.Label(target).Named("TARGET")));
    public void Loaded(string mode, ModWorldMapMarkerDto target)
    {
        CloseProgress();
        Messages.Message(ClashOfRimText.Key(ClientRemoteMapModes.LoadedKey(mode), ClientWorldTargetPresentation.Label(target).Named("TARGET")),
            MessageTypeDefOf.NeutralEvent, historical: false);
    }
    public void LoginFailed(ClashOfRimClientNetworkResult<ModLoginResponseDto> login)
    {
        CloseProgress(); compatibilityMismatch(login.Response);
        Failure(ClashOfRimText.Key("ClashOfRim.Observation.StatusLoginFailed",
            (login.Response?.Result?.Message ?? login.Message ?? ClashOfRimText.Key("ClashOfRim.ServerEntry.StatusLoginFallback")).Named("MESSAGE")), false);
    }
    public void DownloadFailed(ClientRemoteSnapshotFailure failure) => Failure(RemoteSnapshotDownloadValidator.FormatFailure(failure,
        new("ClashOfRim.Observation.StatusDownloadFailed", "ClashOfRim.Observation.StatusMissingSnapshot", "ClashOfRim.Observation.StatusPayloadFailed")), true);
    public void OpenFailed(string message) => Failure(message, true);
    public void Exception(Exception exception, bool opening)
    {
        Failure(ClashOfRimText.Key(opening ? "ClashOfRim.RemoteSessionMap.StatusGenerateException" : "ClashOfRim.Observation.StatusException",
            exception.GetType().Name.Named("TYPE"), exception.Message.Named("MESSAGE")), true);
        Log.Warning("[ClashOfRim] Remote observation " + (opening ? "map open" : "snapshot download") + " failed: " + exception);
    }
    public void CloseProgress() => ServerEntryProgressWindow.CloseCurrent();
    private void Progress(string message)
    {
        status(message);
        ServerEntryProgressWindow.Show(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.LoadingTitle"), message, -1f, false);
    }
    private void Failure(string message, bool closeProgress)
    {
        if (closeProgress) CloseProgress();
        status(message); Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
    }
}
