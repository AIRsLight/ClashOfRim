using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.EventLetters;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool HandleEventLetterAction(string eventId, ClashOfRimEventLetterActionKind actionKind)
        => eventApplications.HandleLetterAction(eventId, actionKind);

    private void PostEventLetters(IReadOnlyCollection<ModEventDetailDto> details, string source) => eventDelivery.Post(details, source);
}
