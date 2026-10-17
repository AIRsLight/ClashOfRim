using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using RimWorld;
using Verse;
using static AIRsLight.ClashOfRim.Diplomacy.ClientDiplomacyPresentation;

namespace AIRsLight.ClashOfRim.EventLetters;

internal sealed class ClientEventLetterRuntime : IClientEventLetterRuntime
{
    private readonly Action<string> refreshWorld;
    internal ClientEventLetterRuntime(Action<string> refreshWorld) => this.refreshWorld = refreshWorld;
    public bool ShouldPost(ModEventDetailDto detail, string? group) => ClientEventLetterPolicy.ShouldPostEventLetter(detail, group);
    public ServerNotificationPayloadSummary? ReadNotification(ModEventDetailDto detail) => ClientEventLetterPolicy.ReadServerNotificationPayloadForDisplay(detail);
    public void RegisterPendingManualEvent(string eventId) => ClashOfRimGameComponent.RegisterPendingManualEvent(eventId);

    public bool ApplyRelation(ClientEventRelationChange change)
    {
        FactionRelationKind relation = ResolveDiplomacyRelationKind(change.EventType);
        if (!PlayerFactionProxyUtility.SetPlayerRelation(change.UserId, relation, DiplomacyRelationReason(change.EventType), canSendHostilityLetter: true)) return false;
        refreshWorld(ClashOfRimText.Key(change.IsResponse ? "ClashOfRim.EventLetter.RefreshReasonDiplomacyResponseNotification" : "ClashOfRim.EventLetter.RefreshReasonDiplomacyRelationChanged"));
        ClashLog.Message($"[ClashOfRim][Diplomacy] Applied event relation: event={change.EventId}, related={change.RelatedEventId ?? "<null>"}, user={change.UserId}, type={change.EventType}, relation={relation}.");
        return true;
    }

    public void ShowLetter(ModEventDetailDto detail, string group, string source)
    {
        string eventId = detail.EventId;
        ClashOfRimEventLetterProjection projection = ClientEventLetterRenderer.BuildEventLetterProjection(detail, group);
        if (projection.HasServerAction)
        {
            ClashOfRimEventChoiceLetter letter = new()
            {
                def = projection.LetterDef,
                ID = Find.UniqueIDsManager.GetNextLetterID(),
                EventId = projection.EventId,
                Actions = projection.Actions.ToList(),
                CanAccept = projection.Actions.Contains(ClashOfRimEventLetterActionKind.Accept),
                CanReject = projection.Actions.Contains(ClashOfRimEventLetterActionKind.Reject),
                lookTargets = projection.LookTargets
            };
            letter.Label = projection.Label;
            letter.title = projection.Label;
            letter.Text = projection.Text;
            Find.LetterStack.ReceiveLetter(letter, $"ClashOfRim event {eventId} from {source}", playSound: true);
        }
        else
        {
            Find.LetterStack.ReceiveLetter(
                projection.Label,
                projection.Text,
                projection.LetterDef,
                projection.LookTargets,
                null,
                null,
                null,
                $"ClashOfRim event {eventId} from {source}",
                playSound: true);
        }

        ClashLog.Message($"[ClashOfRim][EventLetter] posted event={eventId} type={detail.EventType} source={source}.");
    }

    public bool HasOpenLetter(string eventId)
    {
        return Find.LetterStack?.LettersListForReading
            .OfType<ClashOfRimEventChoiceLetter>()
            .Any(letter => string.Equals(letter.EventId, eventId, StringComparison.Ordinal)) == true;
    }

    internal static void RemoveEventLetters(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || Find.LetterStack?.LettersListForReading is null)
        {
            return;
        }

        List<ClashOfRimEventChoiceLetter> letters = Find.LetterStack.LettersListForReading
            .OfType<ClashOfRimEventChoiceLetter>()
            .Where(letter => string.Equals(letter.EventId, eventId, StringComparison.Ordinal))
            .ToList();
        for (int i = 0; i < letters.Count; i++)
        {
            Find.LetterStack.RemoveLetter(letters[i]);
            Find.Archive?.Remove(letters[i]);
        }
    }
}
