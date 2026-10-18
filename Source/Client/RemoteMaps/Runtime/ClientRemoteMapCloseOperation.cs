using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal sealed class ClientRemoteMapCloseOperation : IClientRemoteMapCloseOperation
{
    private static readonly HashSet<MapParent> closing = new();
    private static int raidRemovalDepth;
    internal static bool ExplicitRaidRemoval => raidRemovalDepth > 0;
    private readonly Game? game = Current.Game;
    private readonly WorldObjectsHolder? world = Current.Game?.World?.worldObjects;
    private readonly MapParent parent;
    private readonly RemoteSessionMapParent? temporary;
    private readonly Map? map;
    private readonly ActiveRemoteMapSession? active = ClashOfRimGameComponent.ActiveRemoteMapSession;
    private readonly ActiveRaidBattleSession? battle = ClashOfRimGameComponent.ActiveRaidBattleSession;
    private readonly string sessionId;
    private readonly string eventId;
    private readonly bool eligible;
    private readonly bool ownsActive;
    private bool used;
    private bool removingMap;
    private bool removedParent;

    internal ClientRemoteMapCloseOperation(MapParent parent)
    {
        this.parent = parent; temporary = parent as RemoteSessionMapParent; map = parent.Map;
        sessionId = temporary?.SessionId ?? string.Empty; eventId = temporary?.RelatedEventId ?? string.Empty;
        IsRaidBattle = temporary?.IsRaidBattle == true;
        ownsActive = active is not null && RemoteSessionMapUtility.MatchesSession(temporary, active);
        eligible = world?.Contains(parent) == true && (parent is RemoteSessionMapParent
            || (parent is RemoteColonyMapParent && map is not null && game?.Maps.Contains(map) == true));
    }
    public bool IsRaidBattle { get; }
    public bool HasMap => map is not null && game?.Maps.Contains(map) == true;
    public bool IsCurrent => eligible && game is not null && ReferenceEquals(Current.Game, game)
        && ReferenceEquals(game.World?.worldObjects, world)
        && ReferenceEquals(ClashOfRimGameComponent.ActiveRemoteMapSession, active)
        && ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle)
        && (removedParent || world!.Contains(parent))
        && (ReferenceEquals(parent.Map, map) || (removingMap && parent.Map is null))
        && (map is null || !HasMap || ReferenceEquals(map.Parent, parent))
        && (temporary is null || (temporary.SessionId == sessionId && temporary.RelatedEventId == eventId && temporary.IsRaidBattle == IsRaidBattle));

    public IDisposable? TryEnter()
    {
        if (used || !IsCurrent || !closing.Add(parent)) return null;
        used = true;
        return new Admission(parent);
    }
    public void EndHiddenTraps() => RaidTrapVisibilityController.EndHiddenTrapSession(map!);
    public void ClosePocketMaps(string reason) => RemoteSessionPocketMapUtility.ClosePocketMapsForSource(map, reason);
    public void PrepareCurrentMap() => RemoteSessionMapUtility.PrepareCurrentMapForRemoval(map!);
    public void DiscardMapThings(string reason) => RemoteSessionThingCleanup.DiscardMarkedMapThings(map, temporary, reason);
    public void RemoveMap()
    {
        removingMap = true;
        if (IsRaidBattle) raidRemovalDepth++;
        try
        {
            using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
                game!.DeinitAndRemoveMap(map!, notifyPlayer: false);
        }
        finally { if (IsRaidBattle) raidRemovalDepth--; }
    }
    public void RemovalFailed(Exception exception, string reason)
        => Log.Warning("[ClashOfRim][Raid] Raid battle map removal threw during " + reason + ": " + exception);
    public void DiscardReferencePawns(string reason) => RemoteSessionThingCleanup.DiscardMarkedReferencePawns(temporary, reason);
    public void RemoveParent()
    {
        // Persistent remote colony markers survive unloading their observed map.
        if (temporary is not null) world!.Remove(temporary);
        removedParent = true;
    }
    public void Complete(bool clearActiveSession, string reason)
    {
        // Event IDs may be shared by a later observation. Remove only this map's projection records.
        ClashOfRimGameComponent.RemoveRemoteMapThingIdentities(
            sessionId: map is null ? sessionId : null,
            mapUniqueId: map?.uniqueID.ToString(CultureInfo.InvariantCulture));
        if (clearActiveSession && ownsActive)
        {
            if (IsRaidBattle && battle is not null) ClashOfRimGameComponent.ClearActiveRaidBattleSession(battle.EventId);
            else if (!IsRaidBattle) ClashOfRimGameComponent.ClearActiveObservationSession(sessionId);
        }
        RemoteMapSnapshotProjector.CleanupUnusedRemoteNpcFactions(reason);
        if (IsRaidBattle) ClashLog.Message("[ClashOfRim][Raid] Closed raid battle map for event " + eventId + ": " + reason);
        else Messages.Message(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Closed"), MessageTypeDefOf.NeutralEvent, historical: false);
    }
    private sealed class Admission : IDisposable
    {
        private MapParent? parent;
        internal Admission(MapParent parent) => this.parent = parent;
        public void Dispose() { if (parent is not null) { closing.Remove(parent); parent = null; } }
    }
}
