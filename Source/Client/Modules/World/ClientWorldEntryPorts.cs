using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldEntryStore
{
    void Initialize(string server, string user, string password);
    void Publish(string colonyId, string snapshotId);
}
internal interface IClientWorldSubstrateTransport
{
    Task<ClashOfRimClientNetworkResult<byte[]>> DownloadAsync(ClashOfRimClientNetworkContext context, string configurationId, CancellationToken token);
}
internal interface IClientWorldEntryPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Reject(string message);
    void Failure(string message);
    void Progress(string message, float progress);
    void Scenario(string? message);
    void CloseProgress();
    void ConfirmRegistration(string account, Action accept);
    void Register(string server, string account);
    void Compatibility(ModLoginResponseDto response, string? language, bool languageAccepted, Func<bool> current, Action? accept = null, Action? cancel = null);
    void Enqueue(Action action, Action discard);
}
