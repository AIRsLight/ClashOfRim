using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotReloadPresentation : IClientSnapshotReloadPresentation
{
    private readonly Action<string, ClientSnapshotReloadPurpose?> status;
    private readonly Action<ModLoginResponseDto?> compatibility;
    private object? owner;
    private ServerEntryProgressWindow? window;
    internal ClientSnapshotReloadPresentation(Action<string, ClientSnapshotReloadPurpose?> status, Action<ModLoginResponseDto?> compatibility)
    { this.status = status; this.compatibility = compatibility; }
    public string Text(string key) => ClashOfRimText.Key(key);
    public string LoginFailure(ClashOfRimClientNetworkResult<ModLoginResponseDto> result)
    {
        compatibility(result.Response);
        return ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotRejected",
            (result.Response?.Result?.Message ?? result.Message ?? Text("ClashOfRim.ServerEntry.StatusLoginFallback")).Named("REASON"));
    }
    public string DownloadFailure(ClientColonySnapshotFailure failure) => ClientSnapshotRestorePresentation.Format(failure);
    public string Exception(Exception ex) => ClashOfRimText.Key("ClashOfRim.DownloadExistingSnapshotException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
    internal static (string Message, float Progress) Describe(ClientSnapshotReloadStage stage, string snapshotId) => stage switch
    {
        ClientSnapshotReloadStage.Waiting => (ClashOfRimText.Key("ClashOfRim.SnapshotReload.Waiting"), -1f),
        ClientSnapshotReloadStage.Downloading => (ClashOfRimText.Key("ClashOfRim.DownloadingExistingSnapshot", snapshotId.Named("SNAPSHOT")), 0.1f),
        ClientSnapshotReloadStage.Login => (ClashOfRimText.Key("ClashOfRim.ServerEntry.StageLogin"), 0.2f),
        ClientSnapshotReloadStage.Metadata => (ClashOfRimText.Key("ClashOfRim.ServerEntry.StageMetadata"), 0.35f),
        ClientSnapshotReloadStage.Payload => (ClashOfRimText.Key("ClashOfRim.ServerEntry.StagePayload", snapshotId.Named("SNAPSHOT")), 0.58f),
        ClientSnapshotReloadStage.Loading => (ClashOfRimText.Key("ClashOfRim.ServerEntry.StageLoad"), 0.95f),
        ClientSnapshotReloadStage.Loaded => (ClashOfRimText.Key("ClashOfRim.ExistingSnapshotRestored", snapshotId.Named("SNAPSHOT")), 1f),
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    public void Progress(object caller, ClientSnapshotReloadStage stage, string snapshotId)
    {
        var (message, progress) = Describe(stage, snapshotId);
        status(message, null);
        if (stage == ClientSnapshotReloadStage.Loaded) return;
        Show(caller, Text("ClashOfRim.ServerEntry.Title"), message, progress, false);
    }
    public void Failure(object caller, string message, ClientSnapshotReloadPurpose purpose)
    { status(message, purpose); Show(caller, Text("ClashOfRim.ServerEntry.FailedTitle"), message, 1, true); }
    private void Show(object caller, string title, string message, float progress, bool canClose)
    {
        if (!ReferenceEquals(caller, owner) || window is null)
        {
            ServerEntryProgressWindow.CloseCurrent(); owner = caller;
            window = new(title, message, progress, canClose); Find.WindowStack.Add(window);
        }
        else window.UpdateStatus(title, message, progress, canClose);
    }
    public void Close(object caller)
    { if (!ReferenceEquals(caller, owner)) return; window?.Close(); window = null; owner = null; }
}
