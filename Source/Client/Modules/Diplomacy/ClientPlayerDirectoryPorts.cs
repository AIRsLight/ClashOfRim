using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Diplomacy;

internal sealed record ClientPlayerSelection(string UserId, string ColonyId, string? SnapshotId = null, string? DisplayName = null)
{
    internal static ClientPlayerSelection From(ModPlayerSummaryDto player) => new(player.UserId, player.ColonyId, player.CurrentSnapshotId, player.DisplayName);
}
internal interface IClientPlayerSelectionStore
{
    ClientPlayerSelection Read();
    void Write(ClientPlayerSelection selection);
    void Persist();
}
internal interface IClientPlayerDirectoryTransport
{
    Task<ClashOfRimClientNetworkResult<ModListPlayersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
}
internal interface IClientPlayerDirectoryPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    string Summary(IReadOnlyCollection<ModPlayerSummaryDto> players);
    void Diagnose(Exception exception);
}
internal interface IClientPlayerDirectoryRuntime
{
    void EnsureProxy(string userId);
    void ApplyRelation(string userId, string relation);
}
