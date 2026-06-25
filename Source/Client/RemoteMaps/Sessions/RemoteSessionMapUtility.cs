using System;
using System.Diagnostics;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionMapUtility
{
    private const string MapParentDefName = "ClashOfRim_RemoteSessionMapParent";
    private const string ScoutMapParentDefName = "ClashOfRim_RemoteScoutMapParent";
    private const string RaidObservationMapParentDefName = "ClashOfRim_RemoteRaidObservationMapParent";
    private const string RaidBattleMapParentDefName = "ClashOfRim_RemoteRaidBattleMapParent";
    private static int explicitRaidBattleMapCloseDepth;

    internal static bool ExplicitRaidBattleMapCloseInProgress => explicitRaidBattleMapCloseDepth > 0;

    public static bool TryOpen(RemoteSessionMapOpenRequest request, out RemoteSessionMapOpenResult result)
    {
        if (request.Package is null)
        {
            result = RemoteSessionMapOpenResult.Failed(ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.StatusMissingPackage"));
            return false;
        }

        bool opened = TryOpenSessionMap(
            request.Target,
            request.Mode,
            request.SessionId,
            request.SnapshotId,
            request.RelatedEventId,
            request.Package,
            request.Payload,
            request.CloseExistingObservationSessions,
            request.FailureCleanupReason,
            out RemoteSessionMapParent? mapParent,
            out Map? map,
            out string failureReason);
        result = opened
            ? RemoteSessionMapOpenResult.Opened(mapParent, map)
            : RemoteSessionMapOpenResult.Failed(failureReason);
        return opened;
    }

    private static bool TryOpenSessionMap(
        ModWorldMapMarkerDto target,
        string mode,
        string sessionId,
        string snapshotId,
        string? relatedEventId,
        ModSnapshotPackageMetadataDto package,
        byte[] payload,
        bool closeExistingObservationSessions,
        string failureCleanupReason,
        out RemoteSessionMapParent? mapParent,
        out Map? map,
        out string failureReason)
    {
        mapParent = null;
        map = null;
        failureReason = string.Empty;

        if (target.Tile < 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.StatusInvalidTile");
            return false;
        }

        WorldObjectDef? def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(DefNameForMode(mode))
            ?? DefDatabase<WorldObjectDef>.GetNamedSilentFail(MapParentDefName);
        if (def is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.StatusMissingDef");
            return false;
        }

        if (HasBlockingRemoteSessionMap(closeExistingObservationSessions, out RemoteSessionMapParent? blocking))
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.ActiveSessionBlocksOpen",
                (blocking?.Label ?? string.Empty).Named("SESSION"));
            return false;
        }

        if (closeExistingObservationSessions)
        {
            CloseExistingSessionMaps();
        }

        RemoteSessionMapParent carrier = CreateTemporarySessionMapParent(
            def,
            target,
            mode,
            sessionId,
            snapshotId);
        carrier.RelatedEventId = relatedEventId ?? string.Empty;
        EnsureCarrierFaction(carrier, target);
        mapParent = carrier;

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!RemoteMapSnapshotProjector.TryProject(package, payload, target, carrier, out map, out failureReason))
            {
                RemoveTemporaryCarrier(carrier);
                return false;
            }

            long projectionMs = stopwatch.ElapsedMilliseconds;
            RemoteObservationMapPostProcessor.Apply(map!, carrier, mode, package);
            long totalMs = stopwatch.ElapsedMilliseconds;
            ClashLog.Message("[ClashOfRim][RemoteSession][Timing] open session map mode="
                + mode
                + ", owner="
                + (target.OwnerUserId ?? string.Empty)
                + ", sourceMap="
                + (target.MapId ?? string.Empty)
                + ", projectionMs="
                + projectionMs
                + ", postProcessMs="
                + (totalMs - projectionMs)
                + ", totalMs="
                + totalMs
                + ".");
        }
        catch (Exception ex)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteSessionMap.StatusGenerateException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            if (map is not null && Current.Game?.Maps?.Contains(map) == true)
            {
                RemoteSessionPocketMapUtility.ClosePocketMapsForSource(map, failureCleanupReason);
                PrepareCurrentMapForRemoval(map);
                RemoteSessionThingCleanup.DiscardMarkedMapThings(map, carrier, failureCleanupReason);
                using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
                {
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
                }
            }

            RemoveTemporaryCarrier(carrier);
            return false;
        }

        CameraJumper.TryJump(map!.Center, map, CameraJumper.MovementMode.Cut);
        return true;
    }

    public static void Close(RemoteSessionMapParent? mapParent)
    {
        CloseObservationMap(mapParent);
    }

    public static RemoteSessionMapParent? FindActiveSessionMap(ActiveRemoteMapSession? session = null)
    {
        session ??= ClashOfRimGameComponent.ActiveRemoteMapSession;
        if (session is null || !session.IsActive)
        {
            return null;
        }

        RemoteSessionMapParent? current = Find.CurrentMap?.Parent as RemoteSessionMapParent;
        if (MatchesSession(current, session))
        {
            return current;
        }

        return Find.WorldObjects?.AllWorldObjects
            .OfType<RemoteSessionMapParent>()
            .FirstOrDefault(parent => MatchesSession(parent, session));
    }

    public static bool CloseActiveObservationMap()
    {
        RemoteSessionMapParent? mapParent = FindActiveSessionMap();
        if (mapParent is null || mapParent.IsRaidBattle)
        {
            return false;
        }

        CloseObservationMap(mapParent);
        return true;
    }

    public static void CloseObservationMap(MapParent? mapParent)
    {
        if (mapParent is null)
        {
            return;
        }

        Map? map = mapParent.Map;
        RemoteSessionMapParent? temporary = mapParent as RemoteSessionMapParent;
        if (map is not null && Current.Game?.Maps?.Contains(map) == true)
        {
            RaidTrapVisibilityController.EndHiddenTrapSession(map);
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(map, "remote session map close");
            PrepareCurrentMapForRemoval(map);
            RemoteSessionThingCleanup.DiscardMarkedMapThings(map, temporary, "remote session map close");
            using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
            {
                Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
            }
            RemoteSessionThingCleanup.DiscardMarkedReferencePawns(temporary, "remote session map close");
        }
        RemoteSessionThingCleanup.DiscardMarkedReferencePawns(temporary, "remote session map close");

        if (temporary is not null && Find.WorldObjects?.Contains(temporary) == true)
        {
            Find.WorldObjects.Remove(temporary);
        }

        ClashOfRimGameComponent.RemoveRemoteMapThingIdentities(
            sessionId: temporary?.SessionId,
            relatedEventId: temporary?.RelatedEventId,
            mapUniqueId: map is null ? null : map.uniqueID.ToString());
        ClashOfRimGameComponent.ClearActiveObservationSession(
            temporary?.SessionId);
        RemoteMapSnapshotProjector.CleanupUnusedRemoteNpcFactions("remote session map close");
        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.RemoteSessionMap.Closed"),
            MessageTypeDefOf.NeutralEvent,
            historical: false);
    }

    public static bool CloseRaidBattleMap(string? relatedEventId, string reason, bool clearActiveSession = true)
    {
        WorldObjectsHolder? worldObjects = Current.Game?.World?.worldObjects;
        if (worldObjects is null)
        {
            Log.Warning("[ClashOfRim][Raid] Refused to close raid battle map before world objects were available: event="
                + (relatedEventId ?? string.Empty)
                + ", reason="
                + (reason ?? string.Empty)
                + ".");
            return false;
        }

        RemoteSessionMapParent? battle = worldObjects.AllWorldObjects
            .OfType<RemoteSessionMapParent>()
            .FirstOrDefault(parent =>
                parent.IsRaidBattle
                && (string.IsNullOrWhiteSpace(relatedEventId)
                    || string.Equals(parent.RelatedEventId, relatedEventId, StringComparison.Ordinal)));
        if (battle is null)
        {
            return false;
        }

        Map? map = battle.Map;
        if (map is not null && Current.Game?.Maps?.Contains(map) == true)
        {
            if (!ReferenceEquals(map.Parent, battle))
            {
                Log.Warning("[ClashOfRim][Raid] Refused to close raid battle because its map parent did not match the battle world object: event="
                    + (battle.RelatedEventId ?? string.Empty)
                    + ", map=Map_"
                    + map.uniqueID
                    + ", reason="
                    + (reason ?? string.Empty)
                    + ".");
                return false;
            }

            RaidTrapVisibilityController.EndHiddenTrapSession(map);
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(map, reason);
            PrepareCurrentMapForRemoval(map);
            RemoteSessionThingCleanup.DiscardMarkedMapThings(map, battle, reason);
            explicitRaidBattleMapCloseDepth++;
            try
            {
                using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
                {
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Raid] Raid battle map removal threw during "
                    + (reason ?? string.Empty)
                    + "; continuing session cleanup: "
                    + ex);
            }
            finally
            {
                explicitRaidBattleMapCloseDepth = Math.Max(0, explicitRaidBattleMapCloseDepth - 1);
            }
            RemoteSessionThingCleanup.DiscardMarkedReferencePawns(battle, reason);
        }
        RemoteSessionThingCleanup.DiscardMarkedReferencePawns(battle, reason);

        if (worldObjects.Contains(battle))
        {
            worldObjects.Remove(battle);
        }

        ClashOfRimGameComponent.RemoveRemoteMapThingIdentities(
            sessionId: battle.SessionId,
            relatedEventId: battle.RelatedEventId,
            mapUniqueId: map is null ? null : map.uniqueID.ToString());
        if (clearActiveSession)
        {
            ClashOfRimGameComponent.ClearActiveRaidBattleSession(battle.RelatedEventId);
        }
        RemoteMapSnapshotProjector.CleanupUnusedRemoteNpcFactions(reason);

        ClashLog.Message("[ClashOfRim][Raid] Closed raid battle map for event "
            + (battle.RelatedEventId ?? string.Empty)
            + ": "
            + reason);
        return true;
    }

    private static bool MatchesSession(RemoteSessionMapParent? parent, ActiveRemoteMapSession session)
    {
        if (parent is null)
        {
            return false;
        }

        if (session.IsRaidBattle != parent.IsRaidBattle)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(session.RelatedEventId))
        {
            if (!string.Equals(parent.RelatedEventId, session.RelatedEventId, StringComparison.Ordinal))
            {
                return false;
            }

            if (session.IsRaidBattle)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(session.SessionId)
            && !string.Equals(parent.SessionId, session.SessionId, StringComparison.Ordinal)
            && !string.Equals(parent.RelatedEventId, session.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static void CloseExistingSessionMaps()
    {
        foreach (RemoteSessionMapParent existing in Find.WorldObjects.AllWorldObjects
                     .OfType<RemoteSessionMapParent>()
                     .Where(existing => !existing.IsRaidBattle)
                     .ToList())
        {
            Close(existing);
        }
    }

    private static bool HasBlockingRemoteSessionMap(
        bool closeExistingObservationSessions,
        out RemoteSessionMapParent? blocking)
    {
        blocking = null;
        foreach (RemoteSessionMapParent existing in Find.WorldObjects?.AllWorldObjects
                     .OfType<RemoteSessionMapParent>()
                 ?? Enumerable.Empty<RemoteSessionMapParent>())
        {
            if (existing.IsRaidBattle || !closeExistingObservationSessions)
            {
                blocking = existing;
                return true;
            }
        }

        return false;
    }

    private static RemoteSessionMapParent CreateTemporarySessionMapParent(
        WorldObjectDef def,
        ModWorldMapMarkerDto target,
        string scope,
        string sessionId,
        string snapshotId)
    {
        var mapParent = (RemoteSessionMapParent)WorldObjectMaker.MakeWorldObject(def);
        mapParent.Tile = new PlanetTile(target.Tile, Math.Max(0, target.TileLayerId));
        mapParent.Configure(
            sessionId,
            scope,
            target.OwnerUserId ?? string.Empty,
            target.OwnerColonyId,
            target.WorldObjectId,
            target.MapId,
            snapshotId,
            target.RelatedEventId,
            target.Label);
        Find.WorldObjects.Add(mapParent);
        return mapParent;
    }

    private static void EnsureCarrierFaction(MapParent carrier, ModWorldMapMarkerDto target)
    {
        Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(target.OwnerUserId);
        if (proxy is not null && carrier.Faction != proxy)
        {
            carrier.SetFaction(proxy);
        }
    }

    private static void RemoveTemporaryCarrier(MapParent carrier)
    {
        if (carrier is RemoteSessionMapParent temporary && Find.WorldObjects?.Contains(temporary) == true)
        {
            Find.WorldObjects.Remove(temporary);
        }
    }

    private static string DefNameForMode(string mode)
    {
        if (string.Equals(mode, RemoteSessionMapParent.RaidBattleMode, StringComparison.OrdinalIgnoreCase))
        {
            return RaidBattleMapParentDefName;
        }

        if (string.Equals(mode, RemoteSessionMapParent.RaidObservationMode, StringComparison.OrdinalIgnoreCase))
        {
            return RaidObservationMapParentDefName;
        }

        return ScoutMapParentDefName;
    }

    private static void PrepareCurrentMapForRemoval(Map map)
    {
        if (Current.Game is null || !ReferenceEquals(Current.Game.CurrentMap, map))
        {
            return;
        }

        Map? fallback = Current.Game.Maps
            .Where(candidate => !ReferenceEquals(candidate, map))
            .FirstOrDefault(candidate => candidate.IsPlayerHome)
            ?? Current.Game.Maps.FirstOrDefault(candidate => !ReferenceEquals(candidate, map));
        if (fallback is null)
        {
            return;
        }

        Current.Game.CurrentMap = fallback;
        CameraJumper.TryJump(fallback.Center, fallback, CameraJumper.MovementMode.Cut);
    }
}
