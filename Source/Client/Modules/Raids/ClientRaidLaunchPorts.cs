using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidLaunchCommand(string IdempotencyKey, string EventId, string PreparationId,
    string DefenderSnapshotId, string GuardDeploymentId, ClientRaidTarget Target, ClientRaidForceManifest Force);

// One runtime owns one captured force and its resulting battle. Retries only read
// that battle; they must never reopen a map or repeat pawn movement.
internal interface IClientRaidLaunchRuntime
{
    bool TryCapture(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, out ClientRaidForceManifest? force, out string failure);
    string? EntryFailure();
    void EnqueueOpen(Action action, Action discarded);
    string? Open(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, ClientPreparedRaid prepared);
    bool HasBattle { get; }
    void ApplyReceipt(string snapshotId, ClientRaidServerDeadlines? deadlines);
}

internal interface IClientRaidLaunchTransport
{
    Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientRaidLaunchCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token);
}

internal interface IClientRaidLaunchPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    string PreparationFailure(ClientRaidPreparationFailure failure);
    string TargetLabel(ModWorldMapMarkerDto target);
    void Status(string message);
    void Message(string message, bool rejected);
    void Progress(string message);
    void CloseProgress();
    void Packaging();
    void Diagnose(Exception exception);
}
