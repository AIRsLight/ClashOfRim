using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal static class RemoteRuntimeWorldObjectRegistry
{
    private const string RemoteDefName = "ClashOfRim_RemoteRuntimeWorldObject";
    private const string ColonyDefName = "ClashOfRim_RemoteColonyMapParent";
    private static WorldObjectDef? cachedRemoteDef;
    private static WorldObjectDef? cachedColonyDef;

    private static WorldObjectDef RemoteDef => cachedRemoteDef ??= DefDatabase<WorldObjectDef>.GetNamed(RemoteDefName);

    private static WorldObjectDef ColonyDef => cachedColonyDef ??= DefDatabase<WorldObjectDef>.GetNamed(ColonyDefName);

    public static RemoteWorldObjectApplyResult Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string currentUserId)
    {
        if (Find.WorldObjects is null)
        {
            return new RemoteWorldObjectApplyResult(
                markers?.Count ?? 0,
                desired: 0,
                existingBefore: 0,
                created: 0,
                updated: 0,
                removed: 0,
                skippedCurrentUser: 0,
                skippedInvalid: 0,
                failed: 0,
                "Find.WorldObjects is null");
        }

        Dictionary<string, WorldObject> existingByMarkerId = BuildExistingRemoteObjectLookup(out List<WorldObject> orphanedExisting);
        int existingBefore = existingByMarkerId.Count + orphanedExisting.Count;
        int skippedCurrentUser = 0;
        int skippedInvalid = 0;
        int created = 0;
        int updated = 0;
        int removed = 0;
        int failed = 0;
        List<ModWorldMapMarkerDto> desired = new();
        var desiredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModWorldMapMarkerDto marker in markers ?? Array.Empty<ModWorldMapMarkerDto>())
        {
            if (!IsDisplayableRemoteWorldObjectMarker(marker)
                || marker.Tile < 0
                || string.IsNullOrWhiteSpace(marker.MarkerId))
            {
                skippedInvalid++;
                continue;
            }

            if (string.Equals(marker.OwnerUserId, currentUserId, StringComparison.Ordinal))
            {
                skippedCurrentUser++;
                continue;
            }

            desired.Add(marker);
            desiredIds.Add(marker.MarkerId);
        }

        foreach (WorldObject existing in orphanedExisting)
        {
            Remove(existing);
            removed++;
        }

        List<string>? staleMarkerIds = null;
        foreach (KeyValuePair<string, WorldObject> existing in existingByMarkerId)
        {
            if (desiredIds.Contains(existing.Key))
            {
                continue;
            }

            Remove(existing.Value);
            staleMarkerIds ??= new List<string>();
            staleMarkerIds.Add(existing.Key);
            removed++;
        }

        if (staleMarkerIds is not null)
        {
            foreach (string markerId in staleMarkerIds)
            {
                existingByMarkerId.Remove(markerId);
            }
        }

        foreach (ModWorldMapMarkerDto marker in desired)
        {
            try
            {
                existingByMarkerId.TryGetValue(marker.MarkerId, out WorldObject? existing);
                if (existing is not null
                    && (existing.Tile != marker.Tile || ShouldUseColonyMapParent(marker) != (existing is RemoteColonyMapParent)))
                {
                    Remove(existing);
                    existingByMarkerId.Remove(marker.MarkerId);
                    removed++;
                    existing = null;
                }

                WorldObject worldObject = existing ?? Create(marker);
                if (existing is null)
                {
                    existingByMarkerId[marker.MarkerId] = worldObject;
                    created++;
                }
                else
                {
                    updated++;
                }

                Configure(worldObject, marker);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning("[ClashOfRim] Failed to apply remote world object marker "
                    + DescribeMarker(marker)
                    + ": "
                    + ex);
            }
        }

        return new RemoteWorldObjectApplyResult(
            markers?.Count ?? 0,
            desired.Count,
            existingBefore,
            created,
            updated,
            removed,
            skippedCurrentUser,
            skippedInvalid,
            failed,
            failureReason: null);
    }

    public static void CleanupOrphans()
    {
        if (Find.WorldObjects is null)
        {
            return;
        }

        List<WorldObject>? orphans = null;
        foreach (WorldObject worldObject in AllRemoteObjects())
        {
            if (!string.IsNullOrWhiteSpace(GetMarkerId(worldObject)))
            {
                continue;
            }

            orphans ??= new List<WorldObject>();
            orphans.Add(worldObject);
        }

        if (orphans is null)
        {
            return;
        }

        foreach (WorldObject worldObject in orphans)
        {
            Remove(worldObject);
        }
    }

    private static WorldObject Create(ModWorldMapMarkerDto marker)
    {
        if (ShouldUseColonyMapParent(marker))
        {
            WorldObjectDef colonyDef = ColonyDef;
            WorldObject made = WorldObjectMaker.MakeWorldObject(colonyDef);
            RemoteColonyMapParent remote = made as RemoteColonyMapParent ?? new RemoteColonyMapParent
            {
                def = colonyDef
            };

            remote.Tile = ToPlanetTile(marker);
            Find.WorldObjects.Add(remote);
            return remote;
        }

        WorldObjectDef remoteDef = RemoteDef;
        WorldObject runtimeMade = WorldObjectMaker.MakeWorldObject(remoteDef);
        RemoteRuntimeWorldObject runtime = runtimeMade as RemoteRuntimeWorldObject ?? new RemoteRuntimeWorldObject
        {
            def = remoteDef
        };

        runtime.Tile = ToPlanetTile(marker);
        Find.WorldObjects.Add(runtime);
        return runtime;
    }

    private static void Configure(WorldObject worldObject, ModWorldMapMarkerDto marker)
    {
        if (worldObject is RemoteColonyMapParent colony)
        {
            Configure(colony, marker);
            return;
        }

        if (worldObject is RemoteRuntimeWorldObject runtime)
        {
            Configure(runtime, marker);
        }
    }

    private static void Configure(RemoteColonyMapParent remote, ModWorldMapMarkerDto marker)
    {
        remote.def = ColonyDef;
        remote.Tile = ToPlanetTile(marker);
        remote.Configure(
            marker.MarkerId,
            marker.OwnerUserId,
            marker.OwnerColonyId,
            marker.OwnerFactionName,
            marker.Kind,
            marker.WorldObjectId,
            marker.IconDefName,
            marker.Label,
            marker.MapId,
            marker.SnapshotId,
            marker.RelatedEventId,
            marker.RelationKind,
            marker.OwnerOnline,
            marker.OwnerLastSeenAtUtc,
            marker.CanTrade,
            marker.CanRaid,
            marker.CanReinforce,
            marker.RaidUnavailableReason,
            marker.RaidUnavailableUntilUtc,
            marker.Appearance?.Mode,
            marker.Appearance?.IconDefName,
            marker.Appearance?.ColorDefName,
            marker.Appearance?.ColorHex);

        Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(marker.OwnerUserId, displayFactionName: marker.OwnerFactionName);
        if (proxy is not null && remote.Faction != proxy)
        {
            remote.SetFaction(proxy);
        }
    }

    private static void Configure(RemoteRuntimeWorldObject worldObject, ModWorldMapMarkerDto marker)
    {
        worldObject.def = RemoteDef;
        worldObject.Tile = ToPlanetTile(marker);
        worldObject.Configure(
            marker.MarkerId,
            marker.OwnerUserId,
            marker.OwnerColonyId,
            marker.OwnerFactionName,
            marker.Kind,
            marker.WorldObjectId,
            marker.IconDefName,
            marker.Label,
            marker.MapId,
            marker.SnapshotId,
            marker.RelatedEventId,
            marker.RelationKind,
            marker.OwnerOnline,
            marker.OwnerLastSeenAtUtc,
            marker.CanTrade,
            marker.CanRaid,
            marker.CanReinforce,
            marker.RaidUnavailableReason,
            marker.RaidUnavailableUntilUtc,
            marker.PathTiles);

        Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(marker.OwnerUserId, displayFactionName: marker.OwnerFactionName);
        if (proxy is not null && worldObject.Faction != proxy)
        {
            worldObject.SetFaction(proxy);
        }
    }

    private static Dictionary<string, WorldObject> BuildExistingRemoteObjectLookup(out List<WorldObject> orphanedExisting)
    {
        orphanedExisting = new List<WorldObject>();
        var existingByMarkerId = new Dictionary<string, WorldObject>(StringComparer.Ordinal);
        foreach (WorldObject existing in AllRemoteObjects())
        {
            string markerId = GetMarkerId(existing);
            if (string.IsNullOrWhiteSpace(markerId))
            {
                orphanedExisting.Add(existing);
                continue;
            }

            if (!existingByMarkerId.ContainsKey(markerId))
            {
                existingByMarkerId.Add(markerId, existing);
                continue;
            }

            orphanedExisting.Add(existing);
        }

        return existingByMarkerId;
    }

    private static PlanetTile ToPlanetTile(ModWorldMapMarkerDto marker)
    {
        return new PlanetTile(marker.Tile, Math.Max(0, marker.TileLayerId));
    }

    private static IEnumerable<WorldObject> AllRemoteObjects()
    {
        List<WorldObject>? worldObjects = Find.WorldObjects?.AllWorldObjects;
        if (worldObjects is null)
        {
            yield break;
        }

        foreach (WorldObject item in worldObjects)
        {
            if (item is RemoteRuntimeWorldObject or RemoteColonyMapParent)
            {
                yield return item;
            }
        }
    }

    private static bool IsDisplayableRemoteWorldObjectMarker(ModWorldMapMarkerDto marker)
    {
        return marker.Kind is "TradeableColony" or "ActiveRaidTarget" or "RuntimeCaravan" or "RuntimeShuttle" or "RuntimeTransportPod" or "RuntimeWorldObject";
    }

    private static bool ShouldUseColonyMapParent(ModWorldMapMarkerDto marker)
    {
        return marker.Kind is "TradeableColony" or "ActiveRaidTarget";
    }

    private static string GetMarkerId(WorldObject worldObject)
    {
        return worldObject switch
        {
            RemoteRuntimeWorldObject runtime => runtime.MarkerId,
            RemoteColonyMapParent colony => colony.MarkerId,
            _ => string.Empty
        };
    }

    private static string DescribeMarker(ModWorldMapMarkerDto marker)
    {
        return $"id={marker.MarkerId}, kind={marker.Kind}, owner={marker.OwnerUserId}, colony={marker.OwnerColonyId}, tile={marker.Tile}, label={marker.Label}";
    }

    private static void Remove(WorldObject worldObject)
    {
        try
        {
            Find.WorldObjects.Remove(worldObject);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to remove remote runtime world object: " + ex);
        }
    }
}

internal sealed class RemoteWorldObjectApplyResult
{
    public RemoteWorldObjectApplyResult(
        int received,
        int desired,
        int existingBefore,
        int created,
        int updated,
        int removed,
        int skippedCurrentUser,
        int skippedInvalid,
        int failed,
        string? failureReason)
    {
        Received = received;
        Desired = desired;
        ExistingBefore = existingBefore;
        Created = created;
        Updated = updated;
        Removed = removed;
        SkippedCurrentUser = skippedCurrentUser;
        SkippedInvalid = skippedInvalid;
        Failed = failed;
        FailureReason = failureReason;
    }

    public int Received { get; }

    public int Desired { get; }

    public int ExistingBefore { get; }

    public int Created { get; }

    public int Updated { get; }

    public int Removed { get; }

    public int SkippedCurrentUser { get; }

    public int SkippedInvalid { get; }

    public int Failed { get; }

    public string? FailureReason { get; }

    public override string ToString()
    {
        string suffix = string.IsNullOrWhiteSpace(FailureReason) ? string.Empty : ", reason=" + FailureReason;
        return $"received={Received}, desired={Desired}, existingBefore={ExistingBefore}, created={Created}, updated={Updated}, removed={Removed}, skippedSelf={SkippedCurrentUser}, skippedInvalid={SkippedInvalid}, failed={Failed}{suffix}";
    }
}
