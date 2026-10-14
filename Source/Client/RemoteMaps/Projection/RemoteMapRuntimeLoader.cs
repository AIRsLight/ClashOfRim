using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteMapRuntimeLoader
{
    internal const string ReferencePawnsNodeName = "clashOfRimReferencePawns";
    private static readonly FieldInfo? LoadedObjectDirectoryField =
        typeof(CrossRefHandler).GetField("loadedObjectDirectory", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool TryLoadSingleMap(
        string tempPath,
        MapParent carrier,
        IReadOnlyCollection<Faction> remoteNpcFactions,
        IReadOnlyList<ILoadReferenceable> mappedReferences,
        out Map? map,
        out string failureReason)
    {
        map = null;
        failureReason = string.Empty;
        // Fail before touching engine state when a game update changes the required Scribe contract.
        if (Scribe.mode != LoadSaveMode.Inactive || LoadedObjectDirectoryField is null)
        {
            failureReason = "Remote map loading requires an idle Scribe and its loaded-object directory.";
            return false;
        }
        List<Map>? maps = null;
        List<Pawn>? referencePawns = null;

        try
        {
            Faction? carrierFaction = carrier.Faction;
            if (carrierFaction is not null)
            {
                carrierFaction.leader = null;
            }

            using (RemoteMapProjectionLoadScope.Begin())
            {
                Scribe.loader.InitLoading(tempPath);
                RegisterExternalLoadReference(carrier);
                RegisterExternalLoadReference(carrierFaction);
                foreach (Faction remoteNpcFaction in remoteNpcFactions)
                {
                    RegisterExternalLoadReference(remoteNpcFaction);
                }

                foreach (ILoadReferenceable reference in mappedReferences)
                {
                    RegisterExternalLoadReference(reference);
                }

                Scribe_Collections.Look(ref referencePawns, ReferencePawnsNodeName, LookMode.Deep);
                Scribe_Collections.Look(ref maps, "maps", LookMode.Deep);
                Scribe.loader.FinalizeLoading();
            }
        }
        catch (Exception ex)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteMapProjection.StatusScribeFailed",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            try
            {
                Scribe.ForceStop();
            }
            catch (Exception forceStopEx)
            {
                Log.Warning("[ClashOfRim] Failed to force-stop Scribe after remote map projection failure: " + forceStopEx);
            }

            return false;
        }

        map = maps?.FirstOrDefault(candidate => candidate is not null);
        if (map is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusNoMapLoaded");
            return false;
        }

        map.info.parent = carrier;
        map.generatorDef ??= carrier.MapGeneratorDef;
        RemoteSessionThingCleanup.KeepReferencePawnsForCleanup(referencePawns, carrier as RemoteSessionMapParent);

        if (carrier.HasMap)
        {
            using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
            {
                Current.Game.DeinitAndRemoveMap(carrier.Map, notifyPlayer: false);
            }
        }

        using (RemoteMapProjectionLoadScope.Begin())
        {
            Current.Game.AddMap(map);
            map.FinalizeLoading();
        }

        carrier.FinalizeLoading();
        RemoteSessionThingCleanup.MarkMapThingsForCleanup(map, carrier as RemoteSessionMapParent);
        Current.Game.CurrentMap = map;
        return true;
    }

    private static void RegisterExternalLoadReference(ILoadReferenceable? reference)
    {
        if (reference is null)
        {
            return;
        }

        if (LoadedObjectDirectoryField?.GetValue(Scribe.loader.crossRefs) is LoadedObjectDirectory directory)
        {
            directory.RegisterLoaded(reference);
        }
    }

}
