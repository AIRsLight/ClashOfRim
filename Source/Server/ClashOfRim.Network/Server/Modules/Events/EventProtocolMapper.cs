using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventProtocolMapper
{
    internal static EventCreationResponse ToEventCreationResponse(
        LedgerAppendResult append,
        ProtocolDeliverySemantics deliverySemantics,
        string createdMessage)
    {
        return new EventCreationResponse(
            append.Created
                ? ProtocolResponse.Ok(createdMessage)
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Events.DuplicateCreation")),
            append.Event.EventId,
            deliverySemantics);
    }

    internal static EventParty ToEventParty(ProtocolIdentity identity)
    {
        return new EventParty(identity.UserId, identity.ColonyId);
    }

    internal static EventTargetContext ToEventTargetContext(EventTargetContextDto dto)
    {
        EventLandingMode landingMode = Enum.TryParse(dto.LandingMode, ignoreCase: true, out EventLandingMode parsed)
            ? parsed
            : EventLandingMode.Unspecified;

        return new EventTargetContext(
            dto.WorldObjectId,
            dto.MapUniqueId,
            dto.Tile,
            landingMode);
    }

    internal static EventTargetContextDto? ToEventTargetContextDto(EventTargetContext? context)
    {
        return context is null
            ? null
            : new EventTargetContextDto(
                context.WorldObjectId,
                context.MapUniqueId,
                context.Tile,
                context.LandingMode.ToString());
    }

}
