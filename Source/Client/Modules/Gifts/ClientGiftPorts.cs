using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Gifts;

internal enum ClientGiftSourceKind { Caravan, TransportPods }
internal sealed record ClientGiftCapabilities(bool GiftsEnabled, bool PvpEnabled);
internal sealed record ClientGiftCargo(ClientGiftSourceKind Kind, string Key, int Tile, int TileLayerId, bool HasCargo);
internal sealed record ClientGiftRemoval(bool Success, string Message);
internal sealed record ClientGiftCapture(IReadOnlyList<ModThingReferenceDto> Things, string? Rejection = null);
internal sealed record ClientGiftTarget(string UserId, string ColonyId, string MapId, string WorldObjectId,
    int Tile, int TileLayerId, string Label);
internal sealed record ClientGiftCommand(string IdempotencyKey, ClientGiftTarget Target, bool Forced,
    string? Message, string LandingMode, IReadOnlyList<ModThingReferenceDto> Things);

internal interface IClientGiftSource
{
    ClientGiftCargo Describe();
    ClientGiftCapture Capture(ClashOfRimClientNetworkContext context, bool forced);
    ClientGiftRemoval Remove();
}

internal interface IClientGiftTransport
{
    Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, ClientGiftCommand command,
        ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token);
}

internal interface IClientGiftPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void Status(string message);
    void Message(string message, bool rejected, bool forced = false);
    void Diagnose(Exception exception);
}
