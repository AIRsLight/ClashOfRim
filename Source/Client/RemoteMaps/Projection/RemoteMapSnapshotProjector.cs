using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteMapSnapshotProjector
{
    private const string ReferencePawnsNodeName = "clashOfRimReferencePawns";
    public const string RemoteNpcFactionName = "ClashOfRim remote NPC";
    private const string RemoteNpcFactionNamePrefix = RemoteNpcFactionName + ":";
    private const int MaxReferencePawns = 256;
    private static readonly FieldInfo? LoadedObjectDirectoryField =
        typeof(CrossRefHandler).GetField("loadedObjectDirectory", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? FactionManagerRemoveMethod =
        typeof(FactionManager).GetMethod("Remove", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool TryProject(
        ModSnapshotPackageMetadataDto package,
        byte[] payload,
        ModWorldMapMarkerDto target,
        MapParent carrier,
        out Map? map,
        out string failureReason)
    {
        map = null;
        failureReason = string.Empty;
        Stopwatch stopwatch = Stopwatch.StartNew();
        long lastStageMs = 0L;
        void MarkStage(string stage)
        {
            if (!Prefs.DevMode)
            {
                return;
            }

            long elapsed = stopwatch.ElapsedMilliseconds;
            ClashLog.Message("[ClashOfRim][RemoteMapProjection][Timing] "
                + stage
                + ": +"
                + (elapsed - lastStageMs)
                + "ms total="
                + elapsed
                + "ms target="
                + (target.OwnerUserId ?? string.Empty)
                + "/"
                + (target.OwnerColonyId ?? string.Empty)
                + " map="
                + (target.MapId ?? string.Empty)
                + ".");
            lastStageMs = elapsed;
        }

        if (!TryDecodeOriginalPayload(package, payload, out byte[] originalSaveBytes, out failureReason))
        {
            return false;
        }
        MarkStage("decode");

        XDocument document;
        try
        {
            document = LoadDocument(originalSaveBytes);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteMapProjection.StatusInvalidXml",
                ex.Message.Named("MESSAGE"));
            return false;
        }
        MarkStage("load-xml");

        XElement? sourceMap = FindMap(document, target.MapId);
        if (sourceMap is null)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteMapProjection.StatusMapMissing",
                (target.MapId ?? string.Empty).Named("MAP"));
            return false;
        }

        XElement mapElement = new XElement(sourceMap);
        ISet<string> sourcePlayerFactionLoadIds = BuildSourcePlayerFactionLoadIds(document);
        RemoteFactionProjection factionProjection = BuildRemoteFactionProjection(
            document,
            carrier.Faction,
            sourcePlayerFactionLoadIds);
        IReadOnlyList<RemoteNpcLordSnapshot> sourceNpcLordSnapshots = CaptureRemoteNpcLordSnapshots(
            mapElement,
            factionProjection,
            sourcePlayerFactionLoadIds);
        int localMapId = Find.UniqueIDsManager.GetNextMapID();
        string sourceMapLoadId = "Map_" + (mapElement.Element("uniqueID")?.Value?.Trim() ?? "0");
        SetElement(mapElement, "uniqueID", localMapId.ToString());
        RewriteMapParentReference(mapElement, carrier);
        RewriteMapReferences(mapElement, sourceMapLoadId, "Map_" + localMapId);
        RewriteFactionReferences(mapElement, carrier.Faction, factionProjection, sourcePlayerFactionLoadIds);
        SanitizeGuestFactionReferences(mapElement, carrier.Faction);
        XElement referencePawnsElement = BuildReferencePawnsElement(document, mapElement);
        MarkStage("copy-map-and-reference-pawns");
        RewriteFactionReferences(referencePawnsElement, carrier.Faction, factionProjection, sourcePlayerFactionLoadIds);
        SanitizeGuestFactionReferences(referencePawnsElement, carrier.Faction);
        IReadOnlyList<ILoadReferenceable> mappedReferences = ClashOfRimCompatibilityApi
            .RewriteRemoteMapProjectionReferences(package, document, mapElement)
            .Concat(ClashOfRimCompatibilityApi.RewriteRemoteMapProjectionReferences(package, document, referencePawnsElement))
            .Distinct()
            .ToList();
        SanitizeSingleMapRuntimeState(mapElement);
        ClearUnresolvedGlobalReferences(mapElement);
        SanitizeReferencePawnRuntimeState(referencePawnsElement);
        ClearUnresolvedGlobalReferences(referencePawnsElement);
        ClashOfRimCompatibilityApi.SanitizeRemoteMapProjection(package, mapElement, referencePawnsElement);
        IReadOnlyList<RemoteMapProjectedThingIdentity> projectedThingIdentities =
            RemoteMapProjectedLoadIdRewriter.Rewrite(mapElement, referencePawnsElement);
        if (carrier is RemoteSessionMapParent remoteSessionMapParent)
        {
            remoteSessionMapParent.SetRemoteNpcLordSnapshots(
                ProjectRemoteNpcLordSnapshots(sourceNpcLordSnapshots, projectedThingIdentities));
        }
        MarkStage("rewrite-and-sanitize");

        string tempPath = Path.Combine(Path.GetTempPath(), "clashofrim-remote-map-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            WriteSingleMapDocument(tempPath, mapElement, referencePawnsElement);
            MarkStage("write-temp-save");
            if (!TryLoadSingleMap(tempPath, carrier, factionProjection.RemoteFactions, mappedReferences, out map, out failureReason))
            {
                return false;
            }
            MarkStage("scribe-load-and-finalize");
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
        }

        ClashLog.Message(
            "[ClashOfRim][RemoteMapProjection] Loaded remote map target="
            + target.OwnerUserId
            + "/"
            + target.OwnerColonyId
            + ", sourceMap="
            + target.MapId
            + ", localMap=Map_"
            + map!.uniqueID);
        ClashOfRimGameComponent.RegisterRemoteMapThingIdentities(
            carrier as RemoteSessionMapParent,
            map.uniqueID.ToString(),
            projectedThingIdentities);
        return true;
    }

    private static bool TryLoadSingleMap(
        string tempPath,
        MapParent carrier,
        IReadOnlyCollection<Faction> remoteNpcFactions,
        IReadOnlyList<ILoadReferenceable> mappedReferences,
        out Map? map,
        out string failureReason)
    {
        map = null;
        failureReason = string.Empty;
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

    private static bool TryDecodeOriginalPayload(
        ModSnapshotPackageMetadataDto package,
        byte[] payload,
        out byte[] original,
        out string failureReason)
    {
        original = Array.Empty<byte>();
        failureReason = string.Empty;

        if (package.PayloadBytes > 0 && payload.LongLength != package.PayloadBytes)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusPayloadSizeMismatch");
            return false;
        }

        if (!HashMatches(payload, package.PayloadSha256))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusPayloadHashMismatch");
            return false;
        }

        try
        {
            original = DecodePayload(payload, package.PayloadEncoding);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.RemoteMapProjection.StatusDecodeFailed",
                ex.Message.Named("MESSAGE"));
            return false;
        }

        if (package.OriginalSaveBytes > 0 && original.LongLength != package.OriginalSaveBytes)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusOriginalSizeMismatch");
            return false;
        }

        if (!HashMatches(original, package.OriginalSha256))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.RemoteMapProjection.StatusOriginalHashMismatch");
            return false;
        }

        return true;
    }

    private static byte[] DecodePayload(byte[] payload, string? encoding)
    {
        if (string.Equals(encoding, "RawRws", StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        if (string.Equals(encoding, "GzipRws", StringComparison.OrdinalIgnoreCase))
        {
            using var source = new MemoryStream(payload);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            gzip.CopyTo(target);
            return target.ToArray();
        }

        throw new NotSupportedException(ClashOfRimText.Key(
            "ClashOfRim.RemoteMapProjection.StatusUnsupportedEncoding",
            (encoding ?? string.Empty).Named("ENCODING")));
    }

    private static XDocument LoadDocument(byte[] payload)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit
        };
        using var source = new MemoryStream(payload);
        using XmlReader reader = XmlReader.Create(source, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XElement? FindMap(XDocument document, string? mapId)
    {
        string normalized = NormalizeMapId(mapId);
        IEnumerable<XElement> maps = document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li") ?? Enumerable.Empty<XElement>();

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            XElement? exact = maps.FirstOrDefault(map => string.Equals(
                map.Element("uniqueID")?.Value?.Trim(),
                normalized,
                StringComparison.Ordinal));
            if (exact is not null)
            {
                return exact;
            }
        }

        return maps.FirstOrDefault();
    }

    private static string NormalizeMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        string trimmed = mapId!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }

    private static void SetElement(XElement parent, string name, string value)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.AddFirst(new XElement(name, value));
        }
        else
        {
            element.Value = value;
        }
    }

    private static void RewriteMapParentReference(XElement mapElement, MapParent carrier)
    {
        XElement? mapInfo = mapElement.Element("mapInfo");
        if (mapInfo is null)
        {
            return;
        }

        XElement? parent = mapInfo.Element("parent");
        if (parent is null)
        {
            mapInfo.Add(new XElement("parent", carrier.GetUniqueLoadID()));
        }
        else
        {
            parent.Value = carrier.GetUniqueLoadID();
        }
    }

    private static void RewriteMapReferences(XElement mapElement, string sourceMapLoadId, string localMapLoadId)
    {
        if (string.IsNullOrWhiteSpace(sourceMapLoadId) || string.Equals(sourceMapLoadId, localMapLoadId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (XElement element in mapElement.Descendants())
        {
            if (string.Equals(element.Value.Trim(), sourceMapLoadId, StringComparison.Ordinal))
            {
                element.Value = localMapLoadId;
            }
        }
    }

    private static void RewriteFactionReferences(
        XElement mapElement,
        Faction? ownerFaction,
        RemoteFactionProjection factionProjection,
        ISet<string> sourcePlayerFactionLoadIds)
    {
        string? ownerLoadId = ownerFaction?.GetUniqueLoadID();
        int ownerReferences = 0;
        int npcReferences = 0;
        int unresolvedReferences = 0;
        foreach (XElement element in mapElement.Descendants().Where(IsFactionReferenceElement))
        {
            string value = element.Value.Trim();
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.Ordinal))
            {
                continue;
            }

            if (sourcePlayerFactionLoadIds.Contains(value))
            {
                if (!string.IsNullOrWhiteSpace(ownerLoadId))
                {
                    element.Value = ownerLoadId!;
                    ownerReferences++;
                }
                else
                {
                    element.Value = "null";
                    unresolvedReferences++;
                }

                continue;
            }

            if (factionProjection.FactionsBySourceLoadId.TryGetValue(value, out Faction remoteFaction))
            {
                element.Value = remoteFaction.GetUniqueLoadID();
                npcReferences++;
            }
            else
            {
                element.Value = "null";
                unresolvedReferences++;
            }
        }

        if (ownerReferences > 0 || npcReferences > 0 || unresolvedReferences > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Rewrote faction references: owner="
                + ownerReferences
                + ", remoteNpc="
                + npcReferences
                + ", cleared="
                + unresolvedReferences
                + ", sourcePlayerFactions="
                + sourcePlayerFactionLoadIds.Count
                + ".");
        }
    }

    private static RemoteFactionProjection BuildRemoteFactionProjection(
        XDocument document,
        Faction? ownerFaction,
        ISet<string> sourcePlayerFactionLoadIds)
    {
        CleanupUnusedRemoteNpcFactions("remote faction projection start");

        Dictionary<string, SourceFactionRecord> sourceFactions = ReadSourceFactions(document);
        Dictionary<string, Faction> mappedFactions = new(StringComparer.Ordinal);
        foreach (SourceFactionRecord sourceFaction in sourceFactions.Values)
        {
            if (sourcePlayerFactionLoadIds.Contains(sourceFaction.LoadId))
            {
                continue;
            }

            Faction? localFaction = ResolveLocalBaselineFaction(sourceFaction);
            if (localFaction is null)
            {
                Log.Warning("[ClashOfRim][RemoteMapProjection] Could not resolve remote faction from the local world baseline: source="
                    + sourceFaction.LoadId
                    + ", def="
                    + (sourceFaction.DefName ?? "<null>")
                    + ", name="
                    + (sourceFaction.Name ?? "<null>")
                    + ". The reference will be cleared.");
                continue;
            }

            mappedFactions[sourceFaction.LoadId] = localFaction;
            if (ownerFaction is not null)
            {
                FactionRelationKind defenderRelation = ResolveSourceRelationWithPlayers(
                    sourceFaction,
                    sourceFactions,
                    sourcePlayerFactionLoadIds);
                SetMutualRelationWithoutLookup(localFaction, ownerFaction, defenderRelation);
            }
        }

        return new RemoteFactionProjection(mappedFactions);
    }

    private static Faction? ResolveLocalBaselineFaction(SourceFactionRecord sourceFaction)
    {
        if (Find.World?.factionManager is null || string.IsNullOrWhiteSpace(sourceFaction.DefName))
        {
            return null;
        }

        List<Faction> candidates = Find.World.factionManager.AllFactionsListForReading
            .Where(faction =>
                faction is not null
                && !faction.temporary
                && !faction.IsPlayer
                && string.Equals(faction.def?.defName, sourceFaction.DefName, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sourceFaction.Name))
        {
            Faction? exactName = candidates.FirstOrDefault(faction =>
                string.Equals(faction.Name, sourceFaction.Name, StringComparison.Ordinal));
            if (exactName is not null)
            {
                return exactName;
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static Dictionary<string, SourceFactionRecord> ReadSourceFactions(XDocument document)
    {
        Dictionary<string, SourceFactionRecord> factions = new(StringComparer.Ordinal);
        foreach (XElement faction in document.Root?
                     .Element("game")?
                     .Element("world")?
                     .Element("factionManager")?
                     .Element("allFactions")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? loadIdValue = faction.Element("loadID")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(loadIdValue))
            {
                continue;
            }

            string loadId = "Faction_" + loadIdValue;
            string? defName = faction.Element("def")?.Value?.Trim();
            string? name = faction.Element("name")?.Value?.Trim();
            var relations = new Dictionary<string, FactionRelationKind>(StringComparer.Ordinal);
            foreach (XElement relation in faction.Element("relations")?.Elements("li") ?? Enumerable.Empty<XElement>())
            {
                string? other = relation.Element("other")?.Value?.Trim();
                string? kindValue = relation.Element("kind")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(other)
                    || string.Equals(other, "null", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(kindValue)
                    || !Enum.TryParse(kindValue, ignoreCase: true, out FactionRelationKind kind))
                {
                    continue;
                }

                relations[other!] = kind;
            }

            factions[loadId] = new SourceFactionRecord(loadId, defName, name, relations);
        }

        return factions;
    }

    private static FactionRelationKind ResolveSourceRelationWithPlayers(
        SourceFactionRecord sourceFaction,
        IReadOnlyDictionary<string, SourceFactionRecord> sourceFactions,
        ISet<string> sourcePlayerFactionLoadIds)
    {
        foreach (string playerLoadId in sourcePlayerFactionLoadIds)
        {
            if (sourceFaction.Relations.TryGetValue(playerLoadId, out FactionRelationKind direct))
            {
                return direct;
            }

            if (sourceFactions.TryGetValue(playerLoadId, out SourceFactionRecord playerFaction)
                && playerFaction.Relations.TryGetValue(sourceFaction.LoadId, out FactionRelationKind reverse))
            {
                return reverse;
            }
        }

        FactionDef? def = string.IsNullOrWhiteSpace(sourceFaction.DefName)
            ? null
            : DefDatabase<FactionDef>.GetNamedSilentFail(sourceFaction.DefName!);
        return def?.permanentEnemy == true ? FactionRelationKind.Hostile : FactionRelationKind.Neutral;
    }

    private static FactionRelationKind ResolveLocalPlayerRelation(string? sourceDefName, FactionDef? fallbackDef)
    {
        if (Faction.OfPlayer is not null && Find.World?.factionManager is not null && !string.IsNullOrWhiteSpace(sourceDefName))
        {
            Faction? localFaction = Find.World.factionManager.AllFactionsListForReading.FirstOrDefault(faction =>
                faction is not null
                && !faction.temporary
                && !faction.IsPlayer
                && string.Equals(faction.def?.defName, sourceDefName, StringComparison.Ordinal));
            if (localFaction is not null)
            {
                return localFaction.RelationKindWith(Faction.OfPlayer);
            }
        }

        FactionDef? def = fallbackDef
            ?? (string.IsNullOrWhiteSpace(sourceDefName)
                ? null
                : DefDatabase<FactionDef>.GetNamedSilentFail(sourceDefName!));
        return def?.permanentEnemy == true ? FactionRelationKind.Hostile : FactionRelationKind.Neutral;
    }

    private static bool IsFactionReferenceElement(XElement element)
    {
        string name = element.Name.LocalName;
        return string.Equals(name, "faction", StringComparison.Ordinal)
            || string.Equals(name, "hostFaction", StringComparison.Ordinal)
            || string.Equals(name, "slaveFaction", StringComparison.Ordinal);
    }

    private static void SanitizeGuestFactionReferences(XElement mapElement, Faction? ownerFaction)
    {
        string? ownerLoadId = ownerFaction?.GetUniqueLoadID();
        if (string.IsNullOrWhiteSpace(ownerLoadId))
        {
            return;
        }

        int fixedPrisonerHosts = 0;
        foreach (XElement pawn in mapElement
                     .Descendants("thing")
                     .Concat(mapElement.Descendants("li"))
                     .Where(IsPawnElement)
                     .ToList())
        {
            XElement? guest = pawn.Element("guest");
            if (guest is null || IsNullElement(guest))
            {
                continue;
            }

            string guestStatus = guest.Element("guestStatus")?.Value?.Trim() ?? string.Empty;
            if (!string.Equals(guestStatus, "Prisoner", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XElement? hostFaction = guest.Element("hostFaction");
            string hostFactionValue = hostFaction?.Value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(hostFactionValue)
                && !string.Equals(hostFactionValue, "null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hostFaction is null)
            {
                guest.AddFirst(new XElement("hostFaction", ownerLoadId));
            }
            else
            {
                hostFaction.RemoveAttributes();
                hostFaction.RemoveNodes();
                hostFaction.Value = ownerLoadId;
            }

            fixedPrisonerHosts++;
        }

        if (fixedPrisonerHosts > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Fixed remote prisoner guest host references: "
                + fixedPrisonerHosts
                + ".");
        }
    }

    private static ISet<string> BuildSourcePlayerFactionLoadIds(XDocument document)
    {
        HashSet<string> loadIds = new(StringComparer.Ordinal);
        foreach (XElement faction in document.Root?
            .Element("game")?
            .Element("world")?
            .Element("factionManager")?
            .Element("allFactions")?
            .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? defName = faction.Element("def")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(defName))
            {
                continue;
            }

            FactionDef? def = DefDatabase<FactionDef>.GetNamedSilentFail(defName!);
            if (def?.isPlayer != true)
            {
                continue;
            }

            string? loadId = faction.Element("loadID")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(loadId))
            {
                loadIds.Add("Faction_" + loadId);
            }
        }

        if (loadIds.Count == 0)
        {
            Log.Warning("[ClashOfRim][RemoteMapProjection] Source save did not expose a player faction load ID; remote player-owned pawns may not map to the defender proxy.");
        }

        return loadIds;
    }

    public static bool IsRemoteNpcFaction(Faction? faction)
    {
        return faction is not null
            && faction.temporary
            && (string.Equals(faction.Name, RemoteNpcFactionName, StringComparison.Ordinal)
                || (faction.Name?.StartsWith(RemoteNpcFactionNamePrefix, StringComparison.Ordinal) == true));
    }

    public static int CleanupUnusedRemoteNpcFactions(string reason)
    {
        FactionManager? manager = Find.World?.factionManager;
        if (manager is null)
        {
            return 0;
        }

        if (FactionManagerRemoveMethod is null)
        {
            Log.Warning("[ClashOfRim][RemoteMapProjection] Cannot clean remote NPC factions because the vanilla removal method was not found.");
            return 0;
        }

        List<Faction> candidates = manager.AllFactionsListForReading
            .Where(IsRemoteNpcFaction)
            .Where(faction => !IsRemoteNpcFactionInUse(faction))
            .ToList();
        int removed = 0;
        foreach (Faction faction in candidates)
        {
            try
            {
                FactionManagerRemoveMethod.Invoke(manager, new object[] { faction });
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteMapProjection] Failed to remove unused remote NPC faction "
                    + faction.GetUniqueLoadID()
                    + " after "
                    + (reason ?? string.Empty)
                    + ": "
                    + ex);
            }
        }

        if (removed > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Removed "
                + removed
                + " unused remote NPC factions after "
                + (reason ?? string.Empty)
                + ".");
        }

        return removed;
    }

    private static bool IsRemoteNpcFactionInUse(Faction faction)
    {
        try
        {
            if (PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Any(pawn => pawn is not null && pawn.Faction == faction))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][RemoteMapProjection] Failed to scan pawns before remote NPC faction cleanup: " + ex);
            return true;
        }

        if (Find.Maps is not null)
        {
            foreach (Map map in Find.Maps)
            {
                if (map?.listerThings?.AllThings?.Any(thing => thing.Faction == faction) == true)
                {
                    return true;
                }
            }
        }

        return Find.WorldObjects?.AllWorldObjects?.Any(worldObject => worldObject.Faction == faction) == true;
    }

    public static void EnsureRemoteNpcRelation(Faction faction, Faction other)
    {
        if (faction == other)
        {
            return;
        }

        FactionRelationKind relationKind = other.IsPlayer
            ? ResolveLocalPlayerRelation(faction.def?.defName, faction.def)
            : FactionRelationKind.Neutral;
        SetMutualRelationWithoutLookup(faction, other, relationKind);
    }

    private static void SetMutualRelationWithoutLookup(Faction first, Faction second, FactionRelationKind relationKind)
    {
        if (first == second)
        {
            return;
        }

        SetRelationWithoutLookup(first, second, relationKind);
        SetRelationWithoutLookup(second, first, relationKind);
    }

    private static void SetRelationWithoutLookup(Faction faction, Faction other, FactionRelationKind relationKind)
    {
        if (faction == other)
        {
            return;
        }

        faction.SetRelation(new FactionRelation
        {
            other = other,
            kind = relationKind
        });
    }

    private sealed class RemoteFactionProjection
    {
        public RemoteFactionProjection(Dictionary<string, Faction> factionsBySourceLoadId)
        {
            FactionsBySourceLoadId = factionsBySourceLoadId;
            RemoteFactions = factionsBySourceLoadId.Values.Distinct().ToList();
        }

        public IReadOnlyDictionary<string, Faction> FactionsBySourceLoadId { get; }

        public IReadOnlyCollection<Faction> RemoteFactions { get; }
    }

    private sealed class SourceFactionRecord
    {
        public SourceFactionRecord(
            string loadId,
            string? defName,
            string? name,
            IReadOnlyDictionary<string, FactionRelationKind> relations)
        {
            LoadId = loadId;
            DefName = defName;
            Name = name;
            Relations = relations;
        }

        public string LoadId { get; }

        public string? DefName { get; }

        public string? Name { get; }

        public IReadOnlyDictionary<string, FactionRelationKind> Relations { get; }
    }

    private static IReadOnlyList<RemoteNpcLordSnapshot> CaptureRemoteNpcLordSnapshots(
        XElement mapElement,
        RemoteFactionProjection factionProjection,
        ISet<string> sourcePlayerFactionLoadIds)
    {
        var snapshots = new List<RemoteNpcLordSnapshot>();
        foreach (XElement lord in mapElement
                     .Element("lordManager")?
                     .Element("lords")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string sourceFactionLoadId = lord.Element("faction")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceFactionLoadId)
                || sourcePlayerFactionLoadIds.Contains(sourceFactionLoadId)
                || !factionProjection.FactionsBySourceLoadId.TryGetValue(sourceFactionLoadId, out Faction faction))
            {
                continue;
            }

            XElement? lordJob = lord.Element("lordJob");
            if (lordJob is null || !TryCreateRemoteNpcLordSnapshot(lordJob, out RemoteNpcLordSnapshot? snapshot))
            {
                continue;
            }

            snapshot!.FactionLoadId = faction.GetUniqueLoadID();
            snapshot.PawnLoadIds = ReadLoadIdList(lord.Element("ownedPawns"));
            snapshot.OwnedBuildingLoadIds = ReadLoadIdList(lord.Element("ownedBuildings"));
            if (snapshot.PawnLoadIds.Count == 0
                && snapshot.OwnedBuildingLoadIds.Count == 0
                && snapshot.ThingLoadIds.Count == 0)
            {
                continue;
            }

            snapshots.Add(snapshot);
        }

        if (snapshots.Count > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Captured remote NPC lord snapshots: "
                + snapshots.Count
                + ".");
        }

        return snapshots;
    }

    private static bool TryCreateRemoteNpcLordSnapshot(XElement lordJob, out RemoteNpcLordSnapshot? snapshot)
    {
        snapshot = null;
        string className = SimpleClassName(lordJob.Attribute("Class")?.Value?.Trim() ?? string.Empty);
        switch (className)
        {
            case "LordJob_AssaultColony":
                snapshot = new RemoteNpcLordSnapshot
                {
                    Kind = RemoteNpcLordJobKind.AssaultColony,
                    CanKidnap = ReadBool(lordJob, "canKidnap", true),
                    CanTimeoutOrFlee = ReadBool(lordJob, "canTimeoutOrFlee", true),
                    Sappers = ReadBool(lordJob, "sappers", false),
                    UseAvoidGridSmart = ReadBool(lordJob, "useAvoidGridSmart", false),
                    CanSteal = ReadBool(lordJob, "canSteal", true),
                    Breachers = ReadBool(lordJob, "breaching", false),
                    CanPickUpOpportunisticWeapons = ReadBool(lordJob, "canPickUpOpportunisticWeapons", false)
                };
                return true;
            case "LordJob_StageThenAttack":
                IntRange delay = ReadIntRange(lordJob, "delay", new IntRange(5000, 15000));
                snapshot = new RemoteNpcLordSnapshot
                {
                    Kind = RemoteNpcLordJobKind.StageThenAttack,
                    StageLoc = lordJob.Element("stageLoc")?.Value?.Trim() ?? string.Empty,
                    RaidSeed = ReadInt(lordJob, "raidSeed", 0),
                    CanTimeoutFlee = ReadBool(lordJob, "canTimeoutFlee", false),
                    CanKidnap = ReadBool(lordJob, "canKidnap", false),
                    CanSteal = ReadBool(lordJob, "canSteal", false),
                    DelayMin = delay.min,
                    DelayMax = delay.max
                };
                return true;
            case "LordJob_SleepThenAssaultColony":
                snapshot = new RemoteNpcLordSnapshot
                {
                    Kind = RemoteNpcLordJobKind.SleepThenAssaultColony,
                    SendWokenUpMessage = ReadBool(lordJob, "sendWokenUpMessage", true),
                    AwakeOnClamor = ReadBool(lordJob, "awakeOnClamor", false)
                };
                return true;
            case "LordJob_MechanoidsDefend":
                snapshot = CreateMechanoidDefendSnapshot(lordJob, RemoteNpcLordJobKind.MechanoidsDefend);
                return true;
            case "LordJob_SleepThenMechanoidsDefend":
                snapshot = CreateMechanoidDefendSnapshot(lordJob, RemoteNpcLordJobKind.SleepThenMechanoidsDefend);
                snapshot.AwakeOnClamor = ReadBool(lordJob, "awakeOnClamor", false);
                return true;
            default:
                return false;
        }
    }

    private static RemoteNpcLordSnapshot CreateMechanoidDefendSnapshot(XElement lordJob, RemoteNpcLordJobKind kind)
    {
        return new RemoteNpcLordSnapshot
        {
            Kind = kind,
            ThingLoadIds = ReadLoadIdList(lordJob.Element("things")),
            ThingsToNotifyOnDefeatLoadIds = ReadLoadIdList(lordJob.Element("thingsToNotifyOnDefeat")),
            DefSpot = lordJob.Element("defSpot")?.Value?.Trim() ?? string.Empty,
            DefendRadius = ReadFloat(lordJob, "defendRadius", 0f),
            CanAssaultColony = ReadBool(lordJob, "canAssaultColony", false),
            IsMechCluster = ReadBool(lordJob, "isMechCluster", false)
        };
    }

    private static IReadOnlyList<RemoteNpcLordSnapshot> ProjectRemoteNpcLordSnapshots(
        IReadOnlyList<RemoteNpcLordSnapshot> snapshots,
        IReadOnlyList<RemoteMapProjectedThingIdentity> projectedThingIdentities)
    {
        if (snapshots.Count == 0)
        {
            return Array.Empty<RemoteNpcLordSnapshot>();
        }

        Dictionary<string, string> projectedByOriginalLoadId = projectedThingIdentities
            .GroupBy(identity => "Thing_" + identity.OriginalThingId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => "Thing_" + group.First().ProjectedThingId,
                StringComparer.Ordinal);
        return snapshots
            .Select(snapshot => snapshot.CloneWithProjectedThingIds(projectedByOriginalLoadId))
            .ToList();
    }

    private static List<string> ReadLoadIdList(XElement? root)
    {
        if (root is null)
        {
            return new List<string>();
        }

        return root.Elements("li")
            .Select(element => element.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string SimpleClassName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int lastDot = value.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < value.Length ? value.Substring(lastDot + 1) : value;
    }

    private static bool ReadBool(XElement parent, string name, bool defaultValue)
    {
        string? value = parent.Element(name)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
    }

    private static int ReadInt(XElement parent, string name, int defaultValue)
    {
        string? value = parent.Element(name)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : int.TryParse(value, out int parsed) ? parsed : defaultValue;
    }

    private static float ReadFloat(XElement parent, string name, float defaultValue)
    {
        string? value = parent.Element(name)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : defaultValue;
    }

    private static IntRange ReadIntRange(XElement parent, string name, IntRange defaultValue)
    {
        string? value = parent.Element(name)?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        try
        {
            return ParseHelper.FromString<IntRange>(value!);
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    private static XElement BuildReferencePawnsElement(XDocument document, XElement mapElement)
    {
        Dictionary<string, XElement> worldPawns = ReadWorldPawnElementsByLoadId(document);
        if (worldPawns.Count == 0)
        {
            return new XElement(ReferencePawnsNodeName);
        }

        List<string> initialReferenceIds = CollectThingLoadIds(mapElement)
            .Where(worldPawns.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(initialReferenceIds);
        bool truncated = false;

        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            if (!selectedIds.Add(id))
            {
                continue;
            }

            if (selectedIds.Count >= MaxReferencePawns)
            {
                truncated = pending.Count > 0;
                break;
            }

            foreach (string nested in CollectThingLoadIds(worldPawns[id]))
            {
                if (!selectedIds.Contains(nested) && worldPawns.ContainsKey(nested))
                {
                    pending.Enqueue(nested);
                }
            }
        }

        XElement references = new(ReferencePawnsNodeName);
        foreach (string id in selectedIds)
        {
            references.Add(new XElement(worldPawns[id]));
        }

        if (worldPawns.Count > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Temporary world pawn reference scan: worldPawnPool="
                + worldPawns.Count
                + ", initialMatches="
                + initialReferenceIds.Count
                + ", added="
                + selectedIds.Count
                + ", truncated="
                + truncated
                + ".");
        }

        return references;
    }

    private static Dictionary<string, XElement> ReadWorldPawnElementsByLoadId(XDocument document)
    {
        var pawns = new Dictionary<string, XElement>(StringComparer.Ordinal);
        XElement? worldPawns = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("worldPawns");
        if (worldPawns is null)
        {
            return pawns;
        }

        foreach (XElement pawn in worldPawns
                     .Elements()
                     .SelectMany(group => group.Elements("li"))
                     .Where(IsPawnElement))
        {
            string? id = pawn.Element("id")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            AddPawnReferenceKey(pawns, id!, pawn);
            AddPawnReferenceKey(pawns, "Thing_" + id, pawn);
        }

        return pawns;
    }

    private static void AddPawnReferenceKey(Dictionary<string, XElement> pawns, string key, XElement pawn)
    {
        if (!pawns.ContainsKey(key))
        {
            pawns[key] = pawn;
        }
    }

    private static IEnumerable<string> CollectThingLoadIds(XElement element)
    {
        foreach (XElement descendant in element.Descendants())
        {
            string value = descendant.Value.Trim();
            if (value.StartsWith("Thing_", StringComparison.Ordinal))
            {
                yield return value;
            }
        }
    }

    private static void SanitizeSingleMapRuntimeState(XElement mapElement)
    {
        ResetMapRuntimeManagers(mapElement);
        ResetPawnRuntimeState(mapElement);
        ClearExternalHistoryReferences(mapElement);
    }

    private static void SanitizeReferencePawnRuntimeState(XElement referencePawnsElement)
    {
        ResetPawnRuntimeState(referencePawnsElement);
        ClearExternalHistoryReferences(referencePawnsElement);
    }

    private static void ResetMapRuntimeManagers(XElement mapElement)
    {
        ReplaceElement(
            mapElement,
            "lordManager",
            new XElement("lordManager",
                new XElement("lords"),
                new XElement("stencilDrawers")));
        ReplaceElement(
            mapElement,
            "pawnDestinationReservationManager",
            new XElement("pawnDestinationReservationManager",
                new XElement("reservedDestinations",
                    new XElement("keys"),
                    new XElement("values"))));
        ReplaceElement(
            mapElement,
            "reservationManager",
            new XElement("reservationManager",
                new XElement("reservations")));
        ReplaceElement(
            mapElement,
            "physicalInteractionReservationManager",
            new XElement("physicalInteractionReservationManager",
                new XElement("reservations")));
        ReplaceElement(
            mapElement,
            "attackTargetReservationManager",
            new XElement("attackTargetReservationManager",
                new XElement("reservations")));
        ReplaceElement(
            mapElement,
            "enrouteManager",
            new XElement("enrouteManager",
                new XElement("enroute")));
        ReplaceElement(
            mapElement,
            "tempTerrain",
            new XElement("tempTerrain",
                new XElement("cycleIndex", "0"),
                new XElement("terrainToRemoveCells"),
                new XElement("terrainToRemoveTicks")));

        mapElement.Element("freezeManager")?.Remove();
    }

    private static void ResetPawnRuntimeState(XElement mapElement)
    {
        int clearedLocalPolicyReferences = 0;
        int clearedVirtualRelations = 0;
        foreach (XElement thing in mapElement
                     .Descendants("thing")
                     .Concat(mapElement.Descendants("li"))
                     .Where(IsPawnElement)
                     .ToList())
        {
            ReplaceElement(
                thing,
                "pather",
                new XElement("pather",
                    new XElement("moving", "False")));
            ReplaceElement(
                thing,
                "jobs",
                new XElement("jobs",
                    new XElement("curJob", new XAttribute("IsNull", "True")),
                    new XElement("curDriver", new XAttribute("IsNull", "True")),
                    new XElement("jobQueue", new XElement("jobs")),
                    new XElement("formingCaravanTick", "-1")));
            ReplaceElement(
                thing,
                "stances",
                new XElement("stances",
                    new XElement("stunner",
                        new XElement("showStunMote", "False"),
                        new XElement("adaptationTicksLeft",
                            new XElement("keys"),
                            new XElement("values"))),
                    new XElement("stagger"),
                    new XElement("curStance", new XAttribute("Class", "Stance_Mobile"))));
            ResetMindState(thing);
            clearedVirtualRelations += ClearVirtualRelations(thing);
            clearedLocalPolicyReferences += ClearLocalSaveOnlyPawnReferences(thing);
        }

        if (clearedLocalPolicyReferences > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Cleared pawn local-save references: policies="
                + clearedLocalPolicyReferences
                + ".");
        }

        if (clearedVirtualRelations > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Cleared pawn virtual relation references: records="
                + clearedVirtualRelations
                + ".");
        }
    }

    private static bool IsPawnElement(XElement element)
    {
        return element.Element("kindDef") is not null
            && element.Element("pather") is not null
            && element.Element("jobs") is not null;
    }

    private static bool IsNullElement(XElement element)
    {
        return string.Equals(element.Attribute("IsNull")?.Value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetMindState(XElement thing)
    {
        XElement? mindState = thing.Element("mindState");
        if (mindState is null)
        {
            return;
        }

        SetNullElement(mindState, "meleeThreat");
        SetNullElement(mindState, "enemyTarget");
        SetNullElement(mindState, "knownExploder");
        SetNullElement(mindState, "lastMannedThing");
        SetNullElement(mindState, "droppedWeapon");
        SetElement(mindState, "lastAttackedTarget", "null");
        ReplaceElement(
            mindState,
            "thinkData",
            new XElement("thinkData",
                new XElement("keys"),
                new XElement("values")));
        SetElement(mindState, "lastJobTag", "Misc");
        SetElement(mindState, "lastEngageTargetTick", "-99999");
        SetElement(mindState, "lastAttackTargetTick", "-99999");
        SetElement(mindState, "lastMeleeThreatHarmTick", "-99999");
        SetElement(mindState, "lastRangedHarmTick", "-99999");
        SetElement(mindState, "lastCombatantTick", "-99999");
        SetElement(mindState, "exitMapAfterTick", "-99999");
        SetElement(mindState, "forcedGotoPosition", "(-1000, -1000, -1000)");
        SetDeepNullElement(mindState, "duty");
        SetDeepNullElement(mindState, "breachingTarget");
        SetDeepNullElement(mindState, "resurrectTarget");
        ReplaceElement(
            mindState,
            "babyAutoBreastfeedMoms",
            new XElement("babyAutoBreastfeedMoms",
                new XElement("keys"),
                new XElement("values")));
        ReplaceElement(
            mindState,
            "babyCaravanBreastfeed",
            new XElement("babyCaravanBreastfeed",
                new XElement("keys"),
                new XElement("values")));
    }

    private static int ClearLocalSaveOnlyPawnReferences(XElement pawn)
    {
        int cleared = 0;
        foreach (XElement element in pawn
                     .Descendants()
                     .Where(element => !element.HasElements && IsLocalSaveOnlyLoadId(element.Value))
                     .ToList())
        {
            element.Value = "null";
            cleared++;
        }

        return cleared;
    }

    private static int ClearVirtualRelations(XElement pawn)
    {
        XElement? virtualRelations = pawn.Element("social")?.Element("virtualRelations");
        if (virtualRelations is null || !virtualRelations.HasElements)
        {
            return 0;
        }

        int cleared = virtualRelations.Elements().Count();
        virtualRelations.RemoveNodes();
        return cleared;
    }

    private static bool IsLocalSaveOnlyLoadId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("ApparelPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("DrugPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("FoodPolicy_", StringComparison.Ordinal);
    }

    private static void ClearExternalHistoryReferences(XElement element)
    {
        int combatLogReferences = 0;
        int taleReferences = 0;
        foreach (XElement combatLogEntry in element.Descendants("combatLogEntry").ToList())
        {
            if (!IsNullReferenceValue(combatLogEntry.Value) || combatLogEntry.HasAttributes || combatLogEntry.HasElements)
            {
                combatLogReferences++;
            }

            combatLogEntry.RemoveAttributes();
            combatLogEntry.RemoveNodes();
            combatLogEntry.Value = "null";
        }

        foreach (XElement taleRef in element.Descendants("taleRef").ToList())
        {
            if (!IsNullElement(taleRef))
            {
                taleReferences++;
            }

            taleRef.ReplaceWith(new XElement("taleRef", new XAttribute("IsNull", "True")));
        }

        if (combatLogReferences > 0 || taleReferences > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Cleared external history references: combatLogs="
                + combatLogReferences
                + ", tales="
                + taleReferences
                + ".");
        }
    }

    private static bool IsNullReferenceValue(string? value)
    {
        return string.Equals(value?.Trim(), "null", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceElement(XElement parent, string name, XElement replacement)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(replacement);
        }
        else
        {
            element.ReplaceWith(replacement);
        }
    }

    private static void SetNullElement(XElement parent, string name)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, "null"));
        }
        else
        {
            element.RemoveAttributes();
            element.RemoveNodes();
            element.Value = "null";
        }
    }

    private static void SetDeepNullElement(XElement parent, string name)
    {
        XElement replacement = new(name, new XAttribute("IsNull", "True"));
        ReplaceElement(parent, name, replacement);
    }

    private static void ClearUnresolvedGlobalReferences(XElement mapElement)
    {
        string[] names =
        {
            "sourcePrecept",
            "curOutfit",
            "curAssignedDrugs",
            "battleActive"
        };

        foreach (XElement element in mapElement.Descendants()
                     .Where(element => names.Contains(element.Name.LocalName, StringComparer.Ordinal)))
        {
            element.RemoveAttributes();
            element.RemoveNodes();
            element.Value = "null";
        }
    }

    private static void WriteSingleMapDocument(string path, XElement mapElement, XElement referencePawnsElement)
    {
        var document = new XDocument(
            new XElement("savegame",
                referencePawnsElement,
                new XElement("maps", mapElement)));
        document.Save(path, SaveOptions.DisableFormatting);
    }

    private static bool HashMatches(byte[] payload, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        using SHA256 sha256 = SHA256.Create();
        string actual = ToLowerHex(sha256.ComputeHash(payload));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }
}
