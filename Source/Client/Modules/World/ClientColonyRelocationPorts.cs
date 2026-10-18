using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientColonyRelocationTarget
{
    bool IsCurrent { get; }
    bool IsAvailable { get; }
    bool HasRemoteSession { get; }
    int HomeCount { get; }
    string OldLabel { get; }
    int Tile { get; }
    int Layer { get; }
    string? SettleFailure { get; }
    void Settle();
    void EnqueueSettled(Action action, Action<Exception> failed);
    void AbandonOld();
    bool IsSingleDestination { get; }
}
internal interface IClientColonyRelocationTransport
{
    Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> PreflightAsync(ClashOfRimClientNetworkContext context, int tile, string key, int layer, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> ConfirmAsync(ClashOfRimClientNetworkContext context, string previous, string relocated, int tile, string key, int layer, CancellationToken token);
}
internal interface IClientColonyRelocationPresentation : IClientWorldPreparationPresentation
{
    void Confirm(string colony, int tile, Func<Task> confirm);
    void Message(string text, ClientWorldWriteMessage kind);
    string Blocked(LocalMutationState mutation);
    string SnapshotFailure(SnapshotUploadFailure failure);
}
internal sealed record ClientColonyRelocationCommand(string? Disabled, Action Open);
