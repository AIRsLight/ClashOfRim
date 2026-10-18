using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Support;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidSupportLossSource : IClientRaidSupportLossSource
{
    private readonly Game game;
    private readonly ActiveRaidBattleSession battle;
    private readonly ClashOfRimGameComponent? component;
    private readonly Dictionary<string, Entry> captured = new();
    internal ClientRaidSupportLossSource(ActiveRaidBattleSession battle)
    { game = Current.Game; component = game?.GetComponent<ClashOfRimGameComponent>(); this.battle = battle; }

    public IReadOnlyList<ClientRaidSupportLoss> Capture()
    {
        captured.Clear();
        if (component is null || !ReferenceEquals(Current.Game, game) || !ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle))
            throw new InvalidOperationException("The captured raid battle is no longer active.");
        foreach (var assignment in component.CopyActiveSupportAssignments())
        {
            if (!assignment.AutoReturnOnSettlement || assignment.FinishInProgress) continue;
            var pawn = SupportPawnLookup.FindActive(assignment.PawnThingId);
            if (pawn is null || pawn.Dead || pawn.Destroyed || pawn.Map is null
                || !ClientMapIdentity.MapIdsMatch("Map_" + pawn.Map.uniqueID, battle.ClientMapId)) continue;
            var loss = new ClientRaidSupportLoss(assignment.EventId, assignment.PawnGlobalKey,
                string.IsNullOrWhiteSpace(pawn.LabelShort) ? assignment.PawnLabel : pawn.LabelShort);
            captured.Add(loss.EventId, new(assignment, pawn, loss));
        }
        return captured.Values.Select(entry => entry.Loss).ToArray();
    }
    public void InProgress(ClientRaidSupportLoss loss, bool value)
    {
        if (Owns(loss, out _)) component!.MarkSupportAssignmentInProgress(loss.EventId, value);
    }
    public bool RemoveAndComplete(ClientRaidSupportLoss loss)
    {
        if (!Owns(loss, out var entry)) return false;
        // Never substitute a newly found pawn for the one whose loss was reported.
        var current = SupportPawnLookup.FindActive(entry!.Assignment.PawnThingId);
        if (current is not null && !ReferenceEquals(current, entry.Pawn)) return false;
        if (!entry.Pawn.Destroyed && !PawnExchangeLifecycleService.RemoveFromLocalWorld(entry.Pawn)) return false;
        component!.MarkSupportAssignmentFinished(loss.EventId);
        return true;
    }
    private bool Owns(ClientRaidSupportLoss loss, out Entry? entry)
    {
        entry = null;
        if (component is null || !ReferenceEquals(Current.Game, game)
            || !ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle)
            || !captured.TryGetValue(loss.EventId, out var found) || !ReferenceEquals(found.Loss, loss)) return false;
        entry = found;
        return ReferenceEquals(component.FindSupportAssignmentByEventId(loss.EventId), found.Assignment);
    }
    private sealed record Entry(ActiveSupportPawnAssignment Assignment, Pawn Pawn, ClientRaidSupportLoss Loss);
}
