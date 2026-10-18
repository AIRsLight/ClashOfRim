using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Support;

internal sealed record ClientSupportReturnView(bool Permanent, bool Departing, bool HasPawn, bool CanFinish);
internal sealed record ClientSupportReturnPayload(string EventId, string GlobalKey, string PawnName, string FinishReason,
    bool Dead, ModPawnExchangePackageDto? Package);
internal sealed record ClientSupportReturnCommand(string IdempotencyKey, ClientSupportReturnPayload Payload)
{
    internal static ClientSupportReturnCommand Create(ClashOfRimClientNetworkContext context, ClientSupportReturnPayload payload) =>
        new($"support-finish:{context.UserId}:{context.ColonyId}:{payload.EventId}:{payload.FinishReason}", payload);
}
internal interface IClientSupportReturnRuntime
{
    IClientSupportReturnSource? Find(string eventId);
}
internal interface IClientSupportReturnSource
{
    ClientSupportReturnView View { get; }
    // Returns true when the pawn is already off-map or dead and can be finalized.
    bool Depart(string reason, Action beforeMutation);
    ClientSupportReturnPayload Prepare(ClashOfRimClientNetworkContext context, string reason, Action beforeMutation);
    void Complete();
}
internal interface IClientSupportReturnTransport
{
    Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishAsync(ClashOfRimClientNetworkContext context,
        ClientSupportReturnCommand command, CancellationToken token);
}
