using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(Faction), nameof(Faction.RelationWith))]
public static class RemoteNpcFactionRelationPatch
{
    private static readonly FieldInfo? RelationsField =
        AccessTools.Field(typeof(Faction), "relations");

    public static void Prefix(Faction __instance, Faction other)
    {
        if (__instance is null || other is null || __instance == other)
        {
            return;
        }

        if (RemoteMapSnapshotProjector.IsRemoteNpcFaction(__instance)
            && !HasRelation(__instance, other))
        {
            RemoteMapSnapshotProjector.EnsureRemoteNpcRelation(__instance, other);
        }
        else if (RemoteMapSnapshotProjector.IsRemoteNpcFaction(other)
                 && !HasRelation(__instance, other))
        {
            RemoteMapSnapshotProjector.EnsureRemoteNpcRelation(other, __instance);
        }
    }

    private static bool HasRelation(Faction faction, Faction other)
    {
        if (RelationsField?.GetValue(faction) is not List<FactionRelation> relations)
        {
            return true;
        }

        for (int i = 0; i < relations.Count; i++)
        {
            if (relations[i].other == other)
            {
                return true;
            }
        }

        return false;
    }
}
