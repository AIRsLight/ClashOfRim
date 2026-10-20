using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteMapRuntimeLoader
{
    internal const string ReferencePawnsNodeName = "clashOfRimReferencePawns";
    private static readonly FieldInfo? LoadedObjectDirectoryField =
        typeof(CrossRefHandler).GetField("loadedObjectDirectory", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool TryLoad(
        XDocument document,
        MapParent carrier,
        IReadOnlyList<ILoadReferenceable> mappedReferences,
        out Map? map,
        out string failureReason,
        Action<IEnumerable<ILoadReferenceable>>? rememberReferences = null,
        bool isRoot = true)
    {
        string path = Path.Combine(Path.GetTempPath(), "clashofrim-remote-map-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            document.Save(path, SaveOptions.DisableFormatting);
            return TryLoadFile(path, carrier, mappedReferences, out map, out failureReason, rememberReferences, isRoot);
        }
        finally { File.Delete(path); }
    }

    private static bool TryLoadFile(string tempPath, MapParent carrier, IReadOnlyList<ILoadReferenceable> mappedReferences,
        out Map? map, out string failureReason, Action<IEnumerable<ILoadReferenceable>>? rememberReferences, bool isRoot)
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
        List<PocketMapParent>? pockets = null;
        List<Pawn>? referencePawns = null;
        Map? previousMap = isRoot ? carrier.Map : null;
        var loadedReferences = new List<ILoadReferenceable>();

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
                foreach (ILoadReferenceable reference in mappedReferences.Concat(new ILoadReferenceable?[] { carrier, carrierFaction })
                    .Where(r => r is not null).Cast<ILoadReferenceable>().GroupBy(r => r.GetUniqueLoadID()).Select(g => g.First()))
                {
                    RegisterExternalLoadReference(reference);
                }

                Scribe_Collections.Look(ref referencePawns, ReferencePawnsNodeName, LookMode.Deep);
                Scribe_Collections.Look(ref pockets, "pocketMaps", LookMode.Deep);
                Scribe_Collections.Look(ref maps, "maps", LookMode.Deep);
                // Match native Game loading: PostLoadInit checks parent.HasMap
                // and otherwise clears already-resolved MapPortal.pocketMap links.
                foreach (var pocket in pockets ?? new List<PocketMapParent>()) Find.World.pocketMaps.Add(pocket);
                foreach (var loaded in maps ?? new List<Map>()) Current.Game.AddMap(loaded);
                loadedReferences.AddRange(Scribe.loader.crossRefs.crossReferencingExposables.OfType<ILoadReferenceable>());
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

            // SpawnSetup has not run yet; unregister the incomplete graph without
            // running normal map disposal against uninitialized runtime managers.
            foreach (var loaded in maps ?? new List<Map>()) Current.Game.Maps.Remove(loaded);
            foreach (var pocket in pockets ?? new List<PocketMapParent>()) Find.World.pocketMaps.Remove(pocket);
            Find.ColonistBar.MarkColonistsDirty();

            return false;
        }

        map = maps?.FirstOrDefault(candidate => candidate is not null);
        if (map is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusNoMapLoaded");
            return false;
        }

        if (isRoot) map.info.parent = carrier;
        map.generatorDef ??= map.Parent.MapGeneratorDef;
        RemoteSessionThingCleanup.KeepReferencePawnsForCleanup(referencePawns, carrier as RemoteSessionMapParent);

        if (previousMap is not null)
        {
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(previousMap, "replace remote projection");
            using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
            {
                Current.Game.DeinitAndRemoveMap(previousMap, notifyPlayer: false);
            }
        }

        using (RemoteMapProjectionLoadScope.Begin())
        {
            foreach (var loaded in maps!)
            {
                loaded.generatorDef ??= loaded.Parent.MapGeneratorDef;
                loaded.FinalizeLoading();
                loaded.Parent.FinalizeLoading();
                RemoteSessionThingCleanup.MarkMapThingsForCleanup(loaded, carrier as RemoteSessionMapParent);
            }
        }
        rememberReferences?.Invoke(mappedReferences.Concat(loadedReferences));
        if (isRoot) Current.Game.CurrentMap = map;
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
