using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

public sealed class ClashOfRimEventChoiceLetter : ChoiceLetter
{
    public string EventId = string.Empty;
    public string EventType = string.Empty;
    public List<ClashOfRimEventLetterActionKind> Actions = new();
    public bool CanAccept;
    public bool CanReject;

    public override IEnumerable<DiaOption> Choices
    {
        get
        {
            if (HasAction(ClashOfRimEventLetterActionKind.Accept))
            {
                yield return BuildServerActionOption(
                    ClashOfRimEventLetterActionKind.Accept,
                    ClashOfRimText.Key("ClashOfRim.Accept"));
            }

            if (HasAction(ClashOfRimEventLetterActionKind.Reject))
            {
                yield return BuildServerActionOption(
                    ClashOfRimEventLetterActionKind.Reject,
                    ClashOfRimText.Key("ClashOfRim.Reject"));
            }

            if (HasAction(ClashOfRimEventLetterActionKind.JumpToTarget)
                && lookTargets is not null
                && lookTargets.IsValid)
            {
                yield return Option_JumpToLocationAndPostpone;
            }

            if (HasAction(ClashOfRimEventLetterActionKind.Postpone))
            {
                yield return Option_Postpone;
            }
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref EventId, "clashOfRimEventId", string.Empty);
        Scribe_Values.Look(ref EventType, "clashOfRimEventType", string.Empty);
        Scribe_Collections.Look(ref Actions, "clashOfRimActions", LookMode.Value);
        Scribe_Values.Look(ref CanAccept, "clashOfRimCanAccept", false);
        Scribe_Values.Look(ref CanReject, "clashOfRimCanReject", false);
        Actions ??= new List<ClashOfRimEventLetterActionKind>();
        if (Scribe.mode == LoadSaveMode.PostLoadInit && Actions.Count == 0)
        {
            if (CanAccept)
            {
                Actions.Add(ClashOfRimEventLetterActionKind.Accept);
            }

            if (CanReject)
            {
                Actions.Add(ClashOfRimEventLetterActionKind.Reject);
            }

            if (lookTargets is not null && lookTargets.IsValid)
            {
                Actions.Add(ClashOfRimEventLetterActionKind.JumpToTarget);
            }

            Actions.Add(ClashOfRimEventLetterActionKind.Postpone);
        }
    }

    private bool HasAction(ClashOfRimEventLetterActionKind action)
    {
        return Actions.Contains(action)
            || action == ClashOfRimEventLetterActionKind.Accept && CanAccept
            || action == ClashOfRimEventLetterActionKind.Reject && CanReject;
    }

    private DiaOption BuildServerActionOption(ClashOfRimEventLetterActionKind actionKind, string label)
    {
        DiaOption option = new(label);
        option.resolveTree = true;
        option.action = delegate
        {
            ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
            if (mod is not null && mod.HandleEventLetterAction(EventId, actionKind))
            {
                Find.LetterStack.RemoveLetter(this);
            }
        };
        return option;
    }
}
