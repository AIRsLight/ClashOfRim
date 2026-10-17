using AIRsLight.ClashOfRim.EventLetters;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool HandleEventLetterAction(string eventId, ClashOfRimEventLetterActionKind actionKind)
        => eventApplications.HandleLetterAction(eventId, actionKind);
}
