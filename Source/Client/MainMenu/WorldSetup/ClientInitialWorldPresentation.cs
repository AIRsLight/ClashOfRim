using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientInitialWorldPresentation : ClientWorldWritePresentation, IClientInitialWorldRuntime, IClientInitialWorldPresentation
{
    private readonly Action<string> snapshotStatus;
    private readonly ClientSessionConnection connection;
    internal ClientInitialWorldPresentation(Action<string> status, Action<string> snapshotStatus, ClientSessionConnection connection) : base(status)
    { this.snapshotStatus = snapshotStatus; this.connection = connection; }
    public bool IsPlaying => Current.ProgramState == ProgramState.Playing;
    public bool IsPresenceRunning => connection.IsRunning;
    public void SnapshotStatus(string text) => snapshotStatus(text);
    public string SnapshotFailure(SnapshotUploadFailure failure) => ClientSnapshotUploadPresentation.ToPublicResult(new(null, null, failure)).Message ?? "";
}
