using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

public sealed class ClashOfRimEventLetterProjection
{
    public ClashOfRimEventLetterProjection(
        string eventId,
        string eventType,
        string label,
        string text,
        LetterDef letterDef,
        LookTargets? lookTargets,
        IReadOnlyList<ClashOfRimEventLetterActionKind> actions)
    {
        EventId = eventId;
        EventType = eventType;
        Label = label;
        Text = text;
        LetterDef = letterDef;
        LookTargets = lookTargets;
        Actions = actions;
    }

    public string EventId { get; }

    public string EventType { get; }

    public string Label { get; }

    public string Text { get; }

    public LetterDef LetterDef { get; }

    public LookTargets? LookTargets { get; }

    public IReadOnlyList<ClashOfRimEventLetterActionKind> Actions { get; }

    public bool HasServerAction =>
        Actions.Contains(ClashOfRimEventLetterActionKind.Accept)
        || Actions.Contains(ClashOfRimEventLetterActionKind.Reject);
}
