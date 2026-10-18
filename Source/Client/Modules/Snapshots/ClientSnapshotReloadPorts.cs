using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal interface IClientSnapshotReload
{
    Task StartAsync(ClientSnapshotReloadRequest request, ClientSynchronizationCoordinator.Lease? continuation = null);
}

internal enum ClientSnapshotReloadPurpose { Entry, Raid }
internal sealed record ClientSnapshotReloadRequest(string? ExpectedSnapshotId, ClientSnapshotReloadPurpose Purpose,
    ModWorldConfigurationDto? WorldConfiguration = null);
internal sealed record ClientSnapshotReloadCompleted(ClientRestoredSnapshot Snapshot, ClientSnapshotReloadRequest Request,
    IClientSnapshotGameSession Game, Func<Action, bool> Step);
internal interface IClientSnapshotReloadRuntime
{
    void Enqueue(Action action, Action discarded);
    IClientSnapshotLoadOperation Bind();
}
internal interface IClientSnapshotLoadOperation : IDisposable
{
    bool IsCurrent { get; }
    bool MutationStarted { get; }
    IClientSnapshotGameSession Game { get; }
    Task<bool> LoadAsync(ClientRestoredSnapshot snapshot, Func<Action, bool> apply, CancellationToken token);
}
internal enum ClientSnapshotReloadStage { Waiting, Downloading, Login, Metadata, Payload, Loading, Loaded }
internal interface IClientSnapshotReloadPresentation
{
    void Progress(object owner, ClientSnapshotReloadStage stage, string snapshotId);
    void Close(object owner);
    string Text(string key);
    string LoginFailure(ClashOfRimClientNetworkResult<ModLoginResponseDto> result);
    string DownloadFailure(ClientColonySnapshotFailure failure);
    string Exception(Exception exception);
    void Failure(object owner, string message, ClientSnapshotReloadPurpose purpose);
}
