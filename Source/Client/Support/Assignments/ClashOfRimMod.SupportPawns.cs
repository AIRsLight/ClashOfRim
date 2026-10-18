using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenCaravanSupportPawnMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, IReadOnlyList<Pawn> pawns) =>
        supportActions.OpenMenu(caravan, targets, pawns);

    internal bool TryFinishSupportAssignmentFromQuest(string eventId) => supportReturns.TryStartDeparture(eventId, "QuestExpired");

    internal bool TryStartSupportAssignmentReturnFromQuest(string eventId, string finishReason) => supportReturns.TryStartDeparture(eventId, finishReason);

    internal bool TryConfirmDepartedSupportAssignment(string eventId, string finishReason) => supportReturns.ConfirmAsync(eventId, finishReason) is not null;
}
