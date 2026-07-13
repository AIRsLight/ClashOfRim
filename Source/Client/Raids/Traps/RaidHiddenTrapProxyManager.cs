using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.RemoteMaps;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidHiddenTrapProxyManager
{
    private const string ProxyDefName = "ClashOfRim_HiddenTrapProxy";

    public static int ReplaceHiddenTraps(Map map, IEnumerable<Thing> traps)
    {
        ThingDef? proxyDef = DefDatabase<ThingDef>.GetNamedSilentFail(ProxyDefName);
        if (proxyDef is null)
        {
            Log.Warning("[ClashOfRim][TrapProxy] Missing proxy ThingDef: " + ProxyDefName);
            return 0;
        }

        int replaced = 0;
        foreach (Thing trap in traps.Where(trap => trap.Spawned && trap.Map == map).ToList())
        {
            if (trap is Building_ClashHiddenTrapProxy)
            {
                continue;
            }

            IntVec3 position = trap.Position;
            Rot4 rotation = trap.Rotation;
            Map trapMap = trap.Map;
            string originalTrapId = ResolveOriginalTrapId(trapMap, trap);
            trap.DeSpawn(DestroyMode.Vanish);

            Thing proxyThing = ThingMaker.MakeThing(proxyDef);
            if (proxyThing is not Building_ClashHiddenTrapProxy proxy)
            {
                Log.Warning("[ClashOfRim][TrapProxy] Proxy ThingDef uses unexpected class: " + proxyThing.GetType().FullName);
                GenSpawn.Spawn(trap, position, trapMap, rotation);
                continue;
            }

            proxy.BindOriginalTrap(trap, originalTrapId);
            GenSpawn.Spawn(proxy, position, trapMap, rotation);
            RaidTrapVisibilityController.RegisterHiddenThing(proxy);
            replaced++;
        }

        if (replaced > 0)
        {
            ClashLog.Message("[ClashOfRim][TrapProxy] Replaced hidden traps with proxies: count=" + replaced + ", map=" + map.GetUniqueLoadID());
        }

        return replaced;
    }

    private static string ResolveOriginalTrapId(Map map, Thing trap)
    {
        string projectedThingId = trap.ThingID ?? trap.GetUniqueLoadID();
        return RemoteMapThingIdentityResolver.TryResolveOriginalThingId(
            ClashOfRimGameComponent.CopyRemoteMapThingIdentities(),
            map.GetUniqueLoadID(),
            projectedThingId,
            out string originalThingId)
            ? originalThingId
            : projectedThingId;
    }

    public static int RestoreUntriggeredProxies(Map map)
    {
        int restored = 0;
        foreach (Building_ClashHiddenTrapProxy proxy in map.listerThings.AllThings.OfType<Building_ClashHiddenTrapProxy>().ToList())
        {
            if (!proxy.Sprung && proxy.HasOriginalTrap)
            {
                proxy.RestoreOriginalIfUntriggered();
                restored++;
            }
        }

        return restored;
    }
}
