using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class EventQueueSummaryDto
{
    public EventQueueSummaryDto(
        IReadOnlyList<EventReferenceDto> directlyProcessable,
        IReadOnlyList<EventReferenceDto> waitingForUserChoice,
        IReadOnlyList<EventReferenceDto> deliveredUnconfirmed,
        IReadOnlyList<EventReferenceDto> conflicts,
        IReadOnlyList<EventReferenceDto> rejected)
    {
        DirectlyProcessable = directlyProcessable;
        WaitingForUserChoice = waitingForUserChoice;
        DeliveredUnconfirmed = deliveredUnconfirmed;
        Conflicts = conflicts;
        Rejected = rejected;
    }

    public IReadOnlyList<EventReferenceDto> DirectlyProcessable { get; }

    public IReadOnlyList<EventReferenceDto> WaitingForUserChoice { get; }

    public IReadOnlyList<EventReferenceDto> DeliveredUnconfirmed { get; }

    public IReadOnlyList<EventReferenceDto> Conflicts { get; }

    public IReadOnlyList<EventReferenceDto> Rejected { get; }
}
