using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Events;
using Verse;

namespace AIRsLight.ClashOfRim.EventLetters;

public sealed class ClashOfRimEventChoiceLetter : ChoiceLetter
{
    private static ClientEventApplications? applications;
    internal static void Attach(ClientEventApplications? value) => applications = value;
    public string EventId = string.Empty;
    public List<ClashOfRimEventLetterActionKind> Actions = new();
    public bool CanAccept;
    public bool CanReject;

    public override IEnumerable<DiaOption> Choices
    {
        get
        {
            var owner = applications;
            var game = Current.Game;
            var stack = Find.LetterStack;
            string eventId = EventId;
            bool CurrentLetter() => ReferenceEquals(applications, owner) && ReferenceEquals(Current.Game, game)
                && string.Equals(EventId, eventId, StringComparison.Ordinal)
                && stack?.LettersListForReading.Contains(this) == true;
            var choose = owner?.BindLetterActions(eventId, CurrentLetter);
            if (HasAction(ClashOfRimEventLetterActionKind.Accept))
            {
                yield return BuildServerActionOption(
                    ClashOfRimEventLetterActionKind.Accept,
                    "ClashOfRim.Accept", choose, stack);
            }

            if (HasAction(ClashOfRimEventLetterActionKind.Reject))
            {
                yield return BuildServerActionOption(
                    ClashOfRimEventLetterActionKind.Reject,
                    "ClashOfRim.Reject", choose, stack);
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

    private DiaOption BuildServerActionOption(ClashOfRimEventLetterActionKind actionKind, string labelKey,
        Func<ClashOfRimEventLetterActionKind, bool>? choose, LetterStack? stack)
    {
        DiaOption option = new(ClashOfRimText.Key(labelKey));
        option.resolveTree = true;
        option.action = delegate
        {
            if (choose?.Invoke(actionKind) == true)
            {
                stack!.RemoveLetter(this);
            }
        };
        return option;
    }
}
