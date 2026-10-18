using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal interface IClientObservationTransport : IClientRemoteSnapshotTransport
{
    Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        CancellationToken token, Action<ModLoginResponseDto> captureReceipt);
}
internal interface IClientObservationRuntime
{
    void EnqueueOpen(Action action, Action onDiscard);
    // Opens and registers the engine map as one operation. Null means success.
    string? Open(RemoteSessionMapOpenRequest request);
}
internal interface IClientObservationPresentation
{
    void Reject(string key);
    void Downloading(string mode, ModWorldMapMarkerDto target);
    void Generating(ModWorldMapMarkerDto target);
    void Loaded(string mode, ModWorldMapMarkerDto target);
    void LoginFailed(ClashOfRimClientNetworkResult<ModLoginResponseDto> login);
    void DownloadFailed(ClientRemoteSnapshotFailure failure);
    void OpenFailed(string message);
    void Exception(Exception exception, bool opening);
    void CloseProgress();
}
