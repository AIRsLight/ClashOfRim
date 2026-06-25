using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public static class RaidAttackerSnapshotCleanupEditor
{
    private static readonly HashSet<string> RemoteSessionMapParentDefNames = new(StringComparer.Ordinal)
    {
        "ClashOfRim_RemoteSessionMapParent",
        "ClashOfRim_RemoteScoutMapParent",
        "ClashOfRim_RemoteRaidObservationMapParent",
        "ClashOfRim_RemoteRaidBattleMapParent"
    };

    public static bool TryRemoveRaidBattleState(
        SaveSnapshotPackage attackerPackage,
        string newSnapshotId,
        DateTimeOffset createdAtUtc,
        out SaveSnapshotPackage cleanedPackage,
        out int removedMapCount)
    {
        bool cleaned = TryRemoveRaidBattleState(
            attackerPackage,
            newSnapshotId,
            createdAtUtc,
            out cleanedPackage,
            out RaidAttackerSnapshotCleanupResult cleanupResult);
        removedMapCount = cleanupResult.RemovedMapCount;
        return cleaned;
    }

    public static bool TryRemoveRaidBattleState(
        SaveSnapshotPackage attackerPackage,
        string newSnapshotId,
        DateTimeOffset createdAtUtc,
        out SaveSnapshotPackage cleanedPackage,
        out RaidAttackerSnapshotCleanupResult cleanupResult)
    {
        ArgumentNullException.ThrowIfNull(attackerPackage);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSnapshotId);

        cleanedPackage = attackerPackage;
        cleanupResult = RaidAttackerSnapshotCleanupResult.Empty;

        byte[] originalPayload = Decode(attackerPackage.Payload, attackerPackage.Envelope.PayloadEncoding);
        XDocument document;
        using (var stream = new MemoryStream(originalPayload))
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        HashSet<string> removedWorldObjectLoadIds = RemoveRaidBattleWorldObjects(document);
        HashSet<string> removedMapIds = FindMapUniqueIdsByParent(document, removedWorldObjectLoadIds);
        RaidBattleCleanupState battleState = ReadRaidBattleCleanupState(document);
        RaidAttackerSnapshotCleanupResult extractedResult = BuildCleanupResult(
            attackerPackage.Index,
            removedMapIds,
            battleState);
        HashSet<string> removedMapPawnIds = CollectPawnIdsFromMaps(document, removedMapIds);
        RaidBattleMovedWorldPawns movedWorldPawns = MoveLostPawnsToWorldPawns(document, removedMapIds, extractedResult);
        HashSet<string> deletedRemotePawnIds = new(removedMapPawnIds, StringComparer.Ordinal);
        deletedRemotePawnIds.ExceptWith(movedWorldPawns.MovedLocalIds);
        int cleanedDeletedPawnReferences = CleanupReferencesToDeletedRaidMapPawns(document, deletedRemotePawnIds);
        int removedMapCount = RemoveMapsByParent(document, removedWorldObjectLoadIds);
        int normalizedCurrentMapIndex = NormalizeCurrentMapIndex(document);
        cleanupResult = extractedResult with { RemovedMapCount = removedMapCount };
        int clearedComponents = ClearRaidBattleComponentState(document);
        if (removedWorldObjectLoadIds.Count == 0
            && removedMapCount == 0
            && clearedComponents == 0
            && movedWorldPawns.MovedCount == 0
            && cleanedDeletedPawnReferences == 0
            && normalizedCurrentMapIndex == 0)
        {
            return false;
        }

        string nextLineageToken = Guid.NewGuid().ToString("N");
        UpdateLineageMarker(document, newSnapshotId, nextLineageToken);

        byte[] editedOriginalPayload;
        using (var stream = new MemoryStream())
        {
            document.Save(stream, SaveOptions.DisableFormatting);
            editedOriginalPayload = stream.ToArray();
        }

        byte[] editedPayload = Encode(editedOriginalPayload, attackerPackage.Envelope.PayloadEncoding);
        SnapshotIdentity identity = attackerPackage.Envelope.Identity with { SnapshotId = newSnapshotId };
        SaveSnapshotEnvelope envelope = attackerPackage.Envelope with
        {
            Identity = identity,
            CreatedAtUtc = createdAtUtc,
            SourceFileName = newSnapshotId + ".rws",
            OriginalSaveBytes = editedOriginalPayload.LongLength,
            PayloadBytes = editedPayload.LongLength,
            OriginalSha256 = Sha256Hex(editedOriginalPayload),
            PayloadSha256 = Sha256Hex(editedPayload),
            PreviousSnapshotId = attackerPackage.Envelope.Identity.SnapshotId,
            LineageToken = attackerPackage.Envelope.NextLineageToken,
            NextLineageToken = nextLineageToken
        };

        string tempPath = Path.Combine(Path.GetTempPath(), "clashofrim-raid-attacker-cleanup-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            File.WriteAllBytes(tempPath, editedOriginalPayload);
            SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(tempPath, new SaveIndexReadOptions
            {
                Identity = identity
            });
            cleanedPackage = new SaveSnapshotPackage(envelope, editedPayload, index);
            return true;
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
    }

    private static RaidAttackerSnapshotCleanupResult BuildCleanupResult(
        SaveSnapshotIndex index,
        HashSet<string> removedMapIds,
        RaidBattleCleanupState battleState)
    {
        if (removedMapIds.Count == 0)
        {
            return RaidAttackerSnapshotCleanupResult.Empty;
        }

        IReadOnlyList<PawnSummary> removedMapPawns = index.Pawns
            .Where(pawn => !string.IsNullOrWhiteSpace(pawn.MapUniqueId)
                && removedMapIds.Contains(pawn.MapUniqueId!)
                && pawn.Dead != true)
            .ToList();

        List<RaidBattleLostPawnSummary> attackPawns = removedMapPawns
            .Where(pawn => MatchesAnyLocalId(battleState.AttackPawnThingIds, pawn.LocalId))
            .Select(pawn => new RaidBattleLostPawnSummary(
                pawn.LocalId,
                pawn.GlobalKey,
                pawn.Name,
                pawn.MapUniqueId))
            .GroupBy(pawn => pawn.GlobalKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        List<RaidBattleLostSupportPawnSummary> supportPawns = new();
        foreach (RaidBattleSupportAssignmentSnapshot assignment in battleState.SupportAssignments)
        {
            if (!assignment.AutoReturnOnSettlement || string.IsNullOrWhiteSpace(assignment.EventId))
            {
                continue;
            }

            PawnSummary? pawn = removedMapPawns.FirstOrDefault(candidate =>
                LocalIdsMatch(candidate.LocalId, assignment.PawnThingId)
                || (!string.IsNullOrWhiteSpace(assignment.PawnGlobalKey)
                    && ContainsLocalThingId(assignment.PawnGlobalKey, candidate.LocalId)));
            if (pawn is null)
            {
                continue;
            }

            supportPawns.Add(new RaidBattleLostSupportPawnSummary(
                assignment.EventId,
                string.IsNullOrWhiteSpace(assignment.PawnThingId) ? pawn.LocalId : assignment.PawnThingId,
                string.IsNullOrWhiteSpace(assignment.PawnGlobalKey) ? pawn.GlobalKey : assignment.PawnGlobalKey,
                string.IsNullOrWhiteSpace(assignment.PawnLabel) ? pawn.Name : assignment.PawnLabel,
                pawn.MapUniqueId));
        }

        return new RaidAttackerSnapshotCleanupResult(
            RemovedMapCount: 0,
            attackPawns,
            supportPawns
                .GroupBy(pawn => pawn.EventId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList());
    }

    private static RaidBattleMovedWorldPawns MoveLostPawnsToWorldPawns(
        XDocument document,
        HashSet<string> removedMapIds,
        RaidAttackerSnapshotCleanupResult cleanupResult)
    {
        if (removedMapIds.Count == 0)
        {
            return RaidBattleMovedWorldPawns.Empty;
        }

        HashSet<string> lostLocalIds = cleanupResult.LostAttackPawns
            .Select(pawn => NormalizeThingId(pawn.LocalId))
            .Concat(cleanupResult.LostSupportPawns.Select(pawn => NormalizeThingId(pawn.PawnThingId)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (lostLocalIds.Count == 0)
        {
            return RaidBattleMovedWorldPawns.Empty;
        }

        XElement? pawnsAlive = EnsureWorldPawnList(document, "pawnsAlive");
        if (pawnsAlive is null)
        {
            return RaidBattleMovedWorldPawns.Empty;
        }

        HashSet<string> existingWorldPawnIds = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("worldPawns")?
            .Elements()
            .Elements("li")
            .Select(element => NormalizeThingId(Text(element, "id")))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        int moved = 0;
        int nextGeneLoadId = MaxGeneLoadId(document) + 1;
        HashSet<string> movedLocalIds = new(StringComparer.Ordinal);
        foreach (XElement map in document.Root?
                     .Element("game")?
                     .Element("maps")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? mapUniqueId = Text(map, "uniqueID");
            if (string.IsNullOrWhiteSpace(mapUniqueId) || !removedMapIds.Contains(mapUniqueId!))
            {
                continue;
            }

            foreach (XElement pawn in map
                         .Element("things")?
                         .Elements("thing")
                         .Where(IsPawnElement)
                         .ToList() ?? new List<XElement>())
            {
                string localId = NormalizeThingId(Text(pawn, "id"));
                if (!lostLocalIds.Contains(localId))
                {
                    continue;
                }

                pawn.Remove();
                movedLocalIds.Add(localId);
                if (existingWorldPawnIds.Contains(localId))
                {
                    continue;
                }

                XElement worldPawn = PreparePawnForWorldPawns(pawn);
                RewriteGeneLoadIds(worldPawn, ref nextGeneLoadId);
                pawnsAlive.Add(worldPawn);
                existingWorldPawnIds.Add(localId);
                moved++;
            }
        }

        return new RaidBattleMovedWorldPawns(moved, movedLocalIds);
    }

    private static HashSet<string> CollectPawnIdsFromMaps(XDocument document, HashSet<string> mapIds)
    {
        HashSet<string> pawnIds = new(StringComparer.Ordinal);
        if (mapIds.Count == 0)
        {
            return pawnIds;
        }

        foreach (XElement map in document.Root?
                     .Element("game")?
                     .Element("maps")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? mapUniqueId = Text(map, "uniqueID");
            if (string.IsNullOrWhiteSpace(mapUniqueId) || !mapIds.Contains(mapUniqueId!))
            {
                continue;
            }

            foreach (XElement pawn in map
                         .Element("things")?
                         .Elements("thing")
                         .Where(IsPawnElement) ?? Enumerable.Empty<XElement>())
            {
                string localId = NormalizeThingId(Text(pawn, "id"));
                if (!string.IsNullOrWhiteSpace(localId))
                {
                    pawnIds.Add(localId);
                }
            }
        }

        return pawnIds;
    }

    private static int CleanupReferencesToDeletedRaidMapPawns(XDocument document, HashSet<string> deletedPawnIds)
    {
        if (deletedPawnIds.Count == 0)
        {
            return 0;
        }

        int changed = 0;
        changed += RemoveReferenceListItems(document, deletedPawnIds, "startingAndOptionalPawns");
        changed += RemoveListItemsContainingDeletedPawn(document, deletedPawnIds, "directRelations");
        changed += RemoveListItemsContainingDeletedPawn(document, deletedPawnIds, "memories");
        changed += RemoveLogEntriesContainingDeletedPawn(document, deletedPawnIds);
        changed += CleanupPregnancyApproachDictionaries(document, deletedPawnIds);
        changed += NullDeletedPawnReferenceElements(document, deletedPawnIds);
        return changed;
    }

    private static int RemoveReferenceListItems(XDocument document, HashSet<string> deletedPawnIds, string listName)
    {
        int removed = 0;
        foreach (XElement item in document.Descendants(listName)
                     .Elements("li")
                     .Where(item => ReferencesDeletedPawn(item, deletedPawnIds))
                     .ToList())
        {
            item.Remove();
            removed++;
        }

        return removed;
    }

    private static int RemoveListItemsContainingDeletedPawn(XDocument document, HashSet<string> deletedPawnIds, string listName)
    {
        int removed = 0;
        foreach (XElement item in document.Descendants(listName)
                     .Elements("li")
                     .Where(item => ContainsDeletedPawnReference(item, deletedPawnIds))
                     .ToList())
        {
            item.Remove();
            removed++;
        }

        return removed;
    }

    private static int RemoveLogEntriesContainingDeletedPawn(XDocument document, HashSet<string> deletedPawnIds)
    {
        int removed = 0;
        foreach (XElement item in document.Descendants("li")
                     .Where(item => IsSavedLogEntry(item) && ContainsDeletedPawnReference(item, deletedPawnIds))
                     .ToList())
        {
            item.Remove();
            removed++;
        }

        return removed;
    }

    private static bool IsSavedLogEntry(XElement item)
    {
        string className = item.Attribute("Class")?.Value ?? string.Empty;
        return className.IndexOf("PlayLogEntry", StringComparison.Ordinal) >= 0
            || className.IndexOf("BattleLogEntry", StringComparison.Ordinal) >= 0
            || className.IndexOf("LogEntry", StringComparison.Ordinal) >= 0;
    }

    private static int CleanupPregnancyApproachDictionaries(XDocument document, HashSet<string> deletedPawnIds)
    {
        int removed = 0;
        foreach (XElement dictionary in document.Descendants("pregnancyApproaches"))
        {
            List<XElement> keys = dictionary.Element("keys")?.Elements("li").ToList() ?? new List<XElement>();
            List<XElement> values = dictionary.Element("values")?.Elements("li").ToList() ?? new List<XElement>();
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                if (!ReferencesDeletedPawn(keys[i], deletedPawnIds))
                {
                    continue;
                }

                keys[i].Remove();
                if (i < values.Count)
                {
                    values[i].Remove();
                }

                removed++;
            }
        }

        return removed;
    }

    private static int NullDeletedPawnReferenceElements(XDocument document, HashSet<string> deletedPawnIds)
    {
        HashSet<string> referenceNames = new(StringComparer.Ordinal)
        {
            "pawn",
            "otherPawn",
            "initiator",
            "recipient",
            "initiatorPawn",
            "recipientPawn",
            "victim",
            "instigator",
            "culprit",
            "other",
            "partner",
            "mother",
            "father",
            "bondedPawn"
        };

        int changed = 0;
        foreach (XElement element in document.Descendants()
                     .Where(element => !element.HasElements
                         && referenceNames.Contains(element.Name.LocalName)
                         && ReferencesDeletedPawn(element, deletedPawnIds)))
        {
            element.Value = "null";
            changed++;
        }

        return changed;
    }

    private static bool ContainsDeletedPawnReference(XElement root, HashSet<string> deletedPawnIds)
    {
        return root.DescendantsAndSelf()
            .Where(element => !element.HasElements)
            .Any(element => ReferencesDeletedPawn(element, deletedPawnIds));
    }

    private static bool ReferencesDeletedPawn(XElement element, HashSet<string> deletedPawnIds)
    {
        string value = NormalizeThingId(element.Value);
        return !string.IsNullOrWhiteSpace(value) && deletedPawnIds.Contains(value);
    }

    private static XElement? EnsureWorldPawnList(XDocument document, string listName)
    {
        XElement? game = document.Root?.Element("game");
        XElement? world = game?.Element("world");
        if (world is null)
        {
            return null;
        }

        XElement? worldPawns = world.Element("worldPawns");
        if (worldPawns is null)
        {
            worldPawns = new XElement("worldPawns");
            world.Add(worldPawns);
        }

        XElement? list = worldPawns.Element(listName);
        if (list is not null)
        {
            return list;
        }

        list = new XElement(listName);
        XElement? before = listName == "pawnsAlive"
            ? worldPawns.Element("pawnsMothballed") ?? worldPawns.Element("pawnsDead") ?? worldPawns.Element("gc")
            : null;
        if (before is not null)
        {
            before.AddBeforeSelf(list);
        }
        else
        {
            worldPawns.Add(list);
        }

        return list;
    }

    private static XElement PreparePawnForWorldPawns(XElement sourcePawn)
    {
        XElement pawn = new(sourcePawn)
        {
            Name = "li"
        };

        XAttribute? classAttribute = pawn.Attribute("Class");
        if (string.Equals(classAttribute?.Value, "Pawn", StringComparison.Ordinal))
        {
            classAttribute!.Remove();
        }

        RemoveChildElements(
            pawn,
            "map",
            "pos",
            "rot",
            "spawnedTick",
            "beenRevealed",
            "lastAttackedTarget",
            "lastJobTag");
        SetElement(pawn, "despawnedTick", "-1");
        SetElement(pawn, "targetHolder", "null");
        ReplaceElement(pawn, EmptyJobsElement());
        ReplaceElement(pawn, new XElement("pather", new XAttribute("IsNull", "True")));
        ReplaceElement(pawn, new XElement("drafter", new XAttribute("IsNull", "True")));
        ReplaceElement(pawn, EmptyOwnershipElement());
        SanitizeMindState(pawn.Element("mindState"));

        return pawn;
    }

    private static int MaxGeneLoadId(XDocument document)
    {
        int max = 0;
        foreach (XElement loadId in document.Descendants("loadID"))
        {
            if (int.TryParse(loadId.Value.Trim(), out int parsed) && parsed > max)
            {
                max = parsed;
            }
        }

        return max;
    }

    private static void RewriteGeneLoadIds(XElement pawn, ref int nextGeneLoadId)
    {
        Dictionary<string, string> rewrittenReferences = new(StringComparer.Ordinal);
        foreach (XElement gene in pawn.Descendants("li")
                     .Where(element => IsGeneListItem(element)))
        {
            XElement? loadId = gene.Element("loadID");
            if (loadId is null)
            {
                loadId = new XElement("loadID");
                gene.Add(loadId);
            }

            string oldReference = "Gene_" + loadId.Value.Trim();
            int newLoadId = nextGeneLoadId++;
            loadId.Value = newLoadId.ToString();
            rewrittenReferences[oldReference] = "Gene_" + newLoadId.ToString();
        }

        if (rewrittenReferences.Count == 0)
        {
            return;
        }

        foreach (XElement reference in pawn.Descendants("overriddenByGene"))
        {
            string value = reference.Value.Trim();
            if (rewrittenReferences.TryGetValue(value, out string? rewritten))
            {
                reference.Value = rewritten;
            }
        }
    }

    private static bool IsGeneListItem(XElement element)
    {
        string? parentName = element.Parent?.Name.LocalName;
        return (string.Equals(parentName, "xenogenes", StringComparison.Ordinal)
                || string.Equals(parentName, "endogenes", StringComparison.Ordinal))
            && (element.Element("def") is not null || element.Element("loadID") is not null);
    }

    private static void SanitizeMindState(XElement? mindState)
    {
        if (mindState is null)
        {
            return;
        }

        SetElement(mindState, "meleeThreat", "null");
        SetElement(mindState, "enemyTarget", "null");
        SetElement(mindState, "knownExploder", "null");
        SetElement(mindState, "lastMannedThing", "null");
        SetElement(mindState, "droppedWeapon", "null");
        RemoveChildElements(
            mindState,
            "lastAttackedTarget",
            "lastJobTag",
            "nextMoveOrderIsWait",
            "breachingTarget");
        ReplaceElement(mindState, EmptyKeysValuesElement("thinkData"));
        ReplaceElement(mindState, new XElement("duty", new XAttribute("IsNull", "True")));

        XElement? priorityWork = mindState.Element("priorityWork");
        if (priorityWork is not null)
        {
            SetElement(priorityWork, "prioritizedCell", "(-1000, -1000, -1000)");
        }

        XElement? mentalState = mindState.Element("mentalStateHandler");
        if (mentalState is not null)
        {
            ReplaceElement(mentalState, new XElement("curState", new XAttribute("IsNull", "True")));
        }

        XElement? inspiration = mindState.Element("inspirationHandler");
        if (inspiration is not null)
        {
            ReplaceElement(inspiration, new XElement("curState", new XAttribute("IsNull", "True")));
        }
    }

    private static XElement EmptyJobsElement()
    {
        return new XElement(
            "jobs",
            new XElement("curJob", new XAttribute("IsNull", "True")),
            new XElement("curDriver", new XAttribute("IsNull", "True")),
            new XElement("jobQueue", new XElement("jobs")),
            new XElement("formingCaravanTick", "-1"));
    }

    private static XElement EmptyOwnershipElement()
    {
        return new XElement(
            "ownership",
            new XElement("ownedBed", "null"),
            new XElement("assignedMeditationSpot", "null"),
            new XElement("assignedGrave", "null"),
            new XElement("assignedThrone", "null"),
            new XElement("assignedDeathrestCasket", "null"));
    }

    private static XElement EmptyKeysValuesElement(string name)
    {
        return new XElement(
            name,
            new XElement("keys"),
            new XElement("values"));
    }

    private static void ReplaceElement(XElement parent, XElement replacement)
    {
        XElement? existing = parent.Element(replacement.Name);
        if (existing is null)
        {
            parent.Add(replacement);
        }
        else
        {
            existing.ReplaceWith(replacement);
        }
    }

    private static void RemoveChildElements(XElement parent, params string[] names)
    {
        foreach (string name in names)
        {
            parent.Elements(name).Remove();
        }
    }

    private static bool IsPawnElement(XElement element)
    {
        return string.Equals(element.Attribute("Class")?.Value, "Pawn", StringComparison.Ordinal)
            || element.Element("kindDef") is not null;
    }

    private static bool MatchesAnyLocalId(HashSet<string> keys, string? localId)
    {
        if (string.IsNullOrWhiteSpace(localId))
        {
            return false;
        }

        return keys.Contains(localId!) ||
            (localId!.StartsWith("Thing_", StringComparison.Ordinal)
                ? keys.Contains(localId.Substring("Thing_".Length))
                : keys.Contains("Thing_" + localId));
    }

    private static bool LocalIdsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(NormalizeThingId(left), NormalizeThingId(right), StringComparison.Ordinal);
    }

    private static bool ContainsLocalThingId(string globalKey, string localId)
    {
        return ExpandLocalThingIds(globalKey).Contains(NormalizeThingId(localId), StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExpandLocalThingIds(string globalKey)
    {
        if (string.IsNullOrWhiteSpace(globalKey))
        {
            yield break;
        }

        int thingMarker = globalKey.LastIndexOf("/thing:", StringComparison.Ordinal);
        if (thingMarker >= 0)
        {
            yield return NormalizeThingId(globalKey.Substring(thingMarker + "/thing:".Length));
            yield break;
        }

        int looseMarker = globalKey.LastIndexOf("thing:", StringComparison.Ordinal);
        if (looseMarker >= 0)
        {
            yield return NormalizeThingId(globalKey.Substring(looseMarker + "thing:".Length));
        }
    }

    private static string NormalizeThingId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        return value.StartsWith("Thing_", StringComparison.Ordinal)
            ? value.Substring("Thing_".Length)
            : value;
    }

    private static HashSet<string> RemoveRaidBattleWorldObjects(XDocument document)
    {
        var removedLoadIds = new HashSet<string>(StringComparer.Ordinal);
        List<XElement> worldObjects = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("worldObjects")?
            .Element("worldObjects")?
            .Elements("li")
            .Where(IsRaidBattleSessionWorldObject)
            .ToList() ?? new List<XElement>();

        foreach (XElement worldObject in worldObjects)
        {
            string? id = worldObject.Element("ID")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                removedLoadIds.Add("WorldObject_" + id);
            }

            worldObject.Remove();
        }

        return removedLoadIds;
    }

    private static bool IsRaidBattleSessionWorldObject(XElement element)
    {
        string? def = element.Element("def")?.Value?.Trim();
        string? className = element.Attribute("Class")?.Value;
        if ((def is null || !RemoteSessionMapParentDefNames.Contains(def))
            && className?.IndexOf("RemoteSessionMapParent", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        string? mode = element.Element("clashOfRimMode")?.Value?.Trim();
        return string.Equals(mode, "RaidBattle", StringComparison.OrdinalIgnoreCase);
    }

    private static int RemoveMapsByParent(XDocument document, HashSet<string> removedWorldObjectLoadIds)
    {
        if (removedWorldObjectLoadIds.Count == 0)
        {
            return 0;
        }

        List<XElement> maps = document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li")
            .Where(map => removedWorldObjectLoadIds.Contains(map.Element("mapInfo")?.Element("parent")?.Value?.Trim() ?? string.Empty))
            .ToList() ?? new List<XElement>();

        foreach (XElement map in maps)
        {
            map.Remove();
        }

        return maps.Count;
    }

    private static int NormalizeCurrentMapIndex(XDocument document)
    {
        XElement? game = document.Root?.Element("game");
        XElement? currentMapIndex = game?.Element("currentMapIndex");
        if (game is null || currentMapIndex is null)
        {
            return 0;
        }

        int mapCount = game.Element("maps")?.Elements("li").Count() ?? 0;
        int normalized = mapCount > 0 ? 0 : -1;
        if (!int.TryParse(currentMapIndex.Value.Trim(), out int parsed)
            || parsed < 0
            || parsed >= mapCount)
        {
            string nextValue = normalized.ToString();
            if (!string.Equals(currentMapIndex.Value, nextValue, StringComparison.Ordinal))
            {
                currentMapIndex.Value = nextValue;
                return 1;
            }
        }

        return 0;
    }

    private static HashSet<string> FindMapUniqueIdsByParent(XDocument document, HashSet<string> removedWorldObjectLoadIds)
    {
        HashSet<string> mapIds = new(StringComparer.Ordinal);
        if (removedWorldObjectLoadIds.Count == 0)
        {
            return mapIds;
        }

        foreach (XElement map in document.Root?
                     .Element("game")?
                     .Element("maps")?
                     .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string parent = map.Element("mapInfo")?.Element("parent")?.Value?.Trim() ?? string.Empty;
            if (!removedWorldObjectLoadIds.Contains(parent))
            {
                continue;
            }

            string? mapUniqueId = Text(map, "uniqueID");
            if (!string.IsNullOrWhiteSpace(mapUniqueId))
            {
                mapIds.Add(mapUniqueId!);
            }
        }

        return mapIds;
    }

    private static RaidBattleCleanupState ReadRaidBattleCleanupState(XDocument document)
    {
        XElement? component = document.Descendants("li")
            .FirstOrDefault(element => element.Element("clashOfRimActiveRaidBattleSession") is not null);
        XElement? raid = component?.Element("clashOfRimActiveRaidBattleSession");
        HashSet<string> attackPawnIds = raid?
            .Element("attackPawnThingIds")?
            .Elements("li")
            .Select(element => NormalizeThingId(element.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<RaidBattleSupportAssignmentSnapshot> supportAssignments = component is null
            ? Array.Empty<RaidBattleSupportAssignmentSnapshot>()
            : component
                .Element("clashOfRimActiveSupportAssignments")?
                .Elements("li")
                .Select(ReadSupportAssignment)
                .Where(assignment => assignment is not null)
                .Cast<RaidBattleSupportAssignmentSnapshot>()
                .ToList()
            ?? new List<RaidBattleSupportAssignmentSnapshot>();

        return new RaidBattleCleanupState(attackPawnIds, supportAssignments);
    }

    private static RaidBattleSupportAssignmentSnapshot? ReadSupportAssignment(XElement element)
    {
        string? eventId = Text(element, "eventId");
        string? pawnThingId = Text(element, "pawnThingId");
        string? pawnGlobalKey = Text(element, "pawnGlobalKey");
        if (string.IsNullOrWhiteSpace(eventId)
            || (string.IsNullOrWhiteSpace(pawnThingId) && string.IsNullOrWhiteSpace(pawnGlobalKey)))
        {
            return null;
        }

        return new RaidBattleSupportAssignmentSnapshot(
            eventId!,
            pawnThingId ?? string.Empty,
            pawnGlobalKey ?? string.Empty,
            Text(element, "pawnLabel"),
            Bool(element, "autoReturnOnSettlement"));
    }

    private static int ClearRaidBattleComponentState(XDocument document)
    {
        int cleared = 0;
        foreach (XElement component in document.Descendants("li")
                     .Where(element => element.Element("clashOfRimActiveRaidBattleSession") is not null
                         || element.Element("clashOfRimActiveRemoteMapSession") is not null)
                     .ToList())
        {
            bool changed = false;
            XElement? activeRaid = component.Element("clashOfRimActiveRaidBattleSession");
            if (activeRaid is not null)
            {
                activeRaid.Remove();
                changed = true;
            }

            XElement? remote = component.Element("clashOfRimActiveRemoteMapSession");
            if (remote?.Element("kind")?.Value?.Trim() == "RaidBattle")
            {
                remote.Remove();
                changed = true;
            }

            if (changed)
            {
                cleared++;
            }
        }

        return cleared;
    }

    private static void UpdateLineageMarker(XDocument document, string snapshotId, string token)
    {
        foreach (XElement component in document.Root?
            .Element("game")?
            .Element("components")?
            .Elements("li") ?? Enumerable.Empty<XElement>())
        {
            string? className = component.Attribute("Class")?.Value;
            if (className is null || className.IndexOf("AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            SetElement(component, "clashOfRimLineageSnapshotId", snapshotId);
            SetElement(component, "clashOfRimLineageToken", token);
        }
    }

    private static void SetElement(XElement parent, string name, string value)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
        }
        else
        {
            element.Value = value;
        }
    }

    private static string? Text(XElement? element, string name)
    {
        string? value = element?.Element(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value == "null" ? null : value;
    }

    private static bool Bool(XElement element, string name)
    {
        string? value = Text(element, name);
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    private static byte[] Decode(byte[] payload, SnapshotPayloadEncoding encoding)
    {
        if (encoding == SnapshotPayloadEncoding.RawRws)
        {
            return payload;
        }

        using var source = new MemoryStream(payload);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }

    private static byte[] Encode(byte[] payload, SnapshotPayloadEncoding encoding)
    {
        if (encoding == SnapshotPayloadEncoding.RawRws)
        {
            return payload;
        }

        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return target.ToArray();
    }

    private static string Sha256Hex(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed record RaidBattleCleanupState(
        HashSet<string> AttackPawnThingIds,
        IReadOnlyList<RaidBattleSupportAssignmentSnapshot> SupportAssignments);

    private sealed record RaidBattleSupportAssignmentSnapshot(
        string EventId,
        string PawnThingId,
        string PawnGlobalKey,
        string? PawnLabel,
        bool AutoReturnOnSettlement);

    private sealed record RaidBattleMovedWorldPawns(
        int MovedCount,
        HashSet<string> MovedLocalIds)
    {
        public static RaidBattleMovedWorldPawns Empty { get; } = new(0, new HashSet<string>(StringComparer.Ordinal));
    }
}

public sealed record RaidAttackerSnapshotCleanupResult(
    int RemovedMapCount,
    IReadOnlyList<RaidBattleLostPawnSummary> LostAttackPawns,
    IReadOnlyList<RaidBattleLostSupportPawnSummary> LostSupportPawns)
{
    public static RaidAttackerSnapshotCleanupResult Empty { get; } = new(
        0,
        Array.Empty<RaidBattleLostPawnSummary>(),
        Array.Empty<RaidBattleLostSupportPawnSummary>());
}

public sealed record RaidBattleLostPawnSummary(
    string LocalId,
    string GlobalKey,
    string? Name,
    string? MapUniqueId);

public sealed record RaidBattleLostSupportPawnSummary(
    string EventId,
    string PawnThingId,
    string PawnGlobalKey,
    string? PawnName,
    string? MapUniqueId);
