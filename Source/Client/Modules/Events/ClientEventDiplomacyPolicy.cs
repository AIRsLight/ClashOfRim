using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Events;

internal static class ClientEventDiplomacyPolicy
{
    internal static bool IsDiplomacyEvent(ModEventDetailDto detail) => IsDiplomacyEvent(detail.EventType);

    internal static bool IsDiplomacyEvent(ServerEventType kind) => IsImmediate(kind) || CanRespond(kind);

    internal static bool IsImmediateDiplomacyEvent(ModEventDetailDto detail) =>
        IsImmediate(detail.EventType);

    private static bool IsImmediate(ServerEventType kind) => kind is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation;

    internal static bool CanRespond(ServerEventType kind) => kind is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest;
}
