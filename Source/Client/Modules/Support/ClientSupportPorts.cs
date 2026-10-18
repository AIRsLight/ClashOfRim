using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Support;

internal sealed record ClientSupportTarget(string UserId, string ColonyId, string MapId, string WorldObjectId,
    int Tile, int TileLayerId, string Label, bool AutoReturnOnSettlement);
internal sealed record ClientSupportSelection(int? Days, bool Permanent);
internal sealed record ClientSupportPawn(string GlobalKey, string Name, int SourceTile, string CaravanId, ModPawnExchangePackageDto Package);
internal sealed record ClientSupportCapture(ClientSupportPawn? Pawn, string? Rejection = null);
internal sealed record ClientSupportCommand(string IdempotencyKey, ClientSupportTarget Target, ClientSupportSelection Selection, ClientSupportPawn Pawn);

internal interface IClientSupportSource
{
    ClientSupportCapture Capture(ClashOfRimClientNetworkContext context, ClientSupportTarget target);
    string? Remove(ClientSupportCommand command, Action beforeMutation);
}
internal interface IClientSupportSendingTransport
{
    Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SendAsync(ClashOfRimClientNetworkContext context,
        ClientSupportCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token);
}
internal interface IClientSupportPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    string Busy { get; }
    string BaseSnapshotChanged { get; }
    void Status(string message);
    void Message(string message, bool rejected);
    void Diagnose(Exception exception);
}
