using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public static class RaidSettlementReturnProcessor
{
    public static RaidSettlementReturnResult Process(RaidSettlementReturnRequest? request)
    {
        if (request == null)
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingRequest);
        }

        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingEventId, request);
        }

        if (request.OriginalDefenseSnapshot == null)
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingOriginalSnapshot, request);
        }

        if (request.ReturnedRaidSnapshot == null)
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingReturnedSnapshot, request);
        }

        if (HasMissingIdentity(request.OriginalSnapshotIdentity))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingOriginalSnapshotIdentity, request);
        }

        SnapshotIdentity originalIdentity = request.OriginalDefenseSnapshot.Envelope.Identity;
        if (!IdentityEquals(originalIdentity, request.OriginalSnapshotIdentity))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.OriginalSnapshotIdentityMismatch, request);
        }

        SnapshotIdentity returnedIdentity = request.ReturnedRaidSnapshot.Envelope.Identity;
        bool usesSeparateReturnedMap = !string.IsNullOrWhiteSpace(request.ReturnedMapUniqueId)
            && !string.Equals(request.ReturnedMapUniqueId, request.TargetMapUniqueId, StringComparison.Ordinal);
        if (!usesSeparateReturnedMap && !SameColony(originalIdentity, returnedIdentity))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.ReturnedSnapshotColonyMismatch, request);
        }

        string targetMapUniqueId = NormalizeMapUniqueId(request.TargetMapUniqueId);
        if (string.IsNullOrWhiteSpace(targetMapUniqueId))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.MissingTargetMap, request);
        }

        if (!request.OriginalDefenseSnapshot.Index.Maps.Any(map => string.Equals(NormalizeMapUniqueId(map.UniqueId), targetMapUniqueId, StringComparison.Ordinal)))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.TargetMapNotFoundInOriginalSnapshot, request);
        }

        string returnedMapUniqueId = string.IsNullOrWhiteSpace(request.ReturnedMapUniqueId)
            ? targetMapUniqueId
            : NormalizeMapUniqueId(request.ReturnedMapUniqueId);
        if (!request.ReturnedRaidSnapshot.Index.Maps.Any(map => string.Equals(NormalizeMapUniqueId(map.UniqueId), returnedMapUniqueId, StringComparison.Ordinal)))
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.TargetMapNotFoundInReturnedSnapshot, request);
        }

        RaidSettlementPolicy policy;
        try
        {
            policy = new RaidSettlementPolicy(
                request.LossRatio,
                request.EventId,
                request.PackableBuildingDefNames,
                request.BuildingMaxHitPointsByDefName,
                request.StuffHitPointFactorByDefName,
                request.StuffHitPointOffsetByDefName,
                request.MinimumRemainingHitPointsRatio,
                request.IgnoredThingDefNames,
                request.BuildingHitPointsLossRatio,
                request.TrapDefNames);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RaidSettlementReturnResult.Rejected(RaidSettlementReturnResultKind.InvalidLossRatio, request);
        }

        HashSet<string> defenderGrowingZoneCells = GrowingZoneCellsOnMap(
            request.OriginalDefenseSnapshot.Index,
            targetMapUniqueId);
        var originalThings = ThingsOnMap(
            request.OriginalDefenseSnapshot.Index,
            targetMapUniqueId,
            defenderGrowingZoneCells);
        var returnedThings = ThingsOnMap(request.ReturnedRaidSnapshot.Index, returnedMapUniqueId)
            .Where(thing => ShouldIncludeSettlementThing(thing, defenderGrowingZoneCells))
            .Select(thing => thing with { MapUniqueId = targetMapUniqueId })
            .ToList();

        RaidSettlementDiffResult settlement = RaidSettlementDiffer.CompareByDisappearance(
            originalThings,
            returnedThings,
            policy,
            OriginalSettlementThingKeys,
            ReturnedSettlementThingKeys) with
        {
            BattlefieldResidues = CollectBattlefieldResidues(
                request.OriginalDefenseSnapshot,
                targetMapUniqueId,
                request.ReturnedRaidSnapshot,
                returnedMapUniqueId)
        };

        return new RaidSettlementReturnResult(
            RaidSettlementReturnResultKind.Accepted,
            request.EventId,
            originalIdentity,
            returnedIdentity,
            targetMapUniqueId,
            settlement);
    }

    private static IEnumerable<ThingSummary> ThingsOnMap(SaveSnapshotIndex index, string mapUniqueId)
    {
        string normalizedMapUniqueId = NormalizeMapUniqueId(mapUniqueId);
        return index.Things.Where(thing =>
            !thing.IsPawn
            && string.Equals(NormalizeMapUniqueId(thing.MapUniqueId), normalizedMapUniqueId, StringComparison.Ordinal));
    }

    private static IEnumerable<ThingSummary> ThingsOnMap(
        SaveSnapshotIndex index,
        string mapUniqueId,
        IReadOnlySet<string> defenderGrowingZoneCells)
    {
        return ThingsOnMap(index, mapUniqueId)
            .Where(thing => ShouldIncludeSettlementThing(thing, defenderGrowingZoneCells));
    }

    private static HashSet<string> GrowingZoneCellsOnMap(SaveSnapshotIndex index, string mapUniqueId)
    {
        string normalizedMapUniqueId = NormalizeMapUniqueId(mapUniqueId);
        return index.Maps
            .Where(map => string.Equals(NormalizeMapUniqueId(map.UniqueId), normalizedMapUniqueId, StringComparison.Ordinal))
            .SelectMany(map => map.GrowingZoneCells ?? Array.Empty<string>())
            .Select(NormalizeCell)
            .Where(cell => !string.IsNullOrWhiteSpace(cell))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ShouldIncludeSettlementThing(
        ThingSummary thing,
        IReadOnlySet<string> defenderGrowingZoneCells)
    {
        if (!IsPlant(thing))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(thing.Position)
            && defenderGrowingZoneCells.Contains(NormalizeCell(thing.Position));
    }

    private static bool IsPlant(ThingSummary thing)
    {
        return thing.Class?.IndexOf("Plant", StringComparison.OrdinalIgnoreCase) >= 0
            || thing.Def?.StartsWith("Plant_", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IReadOnlyList<RaidSettlementBattlefieldResidue> CollectBattlefieldResidues(
        SaveSnapshotPackage originalDefenseSnapshot,
        string targetMapUniqueId,
        SaveSnapshotPackage returnedRaidSnapshot,
        string returnedMapUniqueId)
    {
        HashSet<string> existingResidueKeys = ThingsOnMap(originalDefenseSnapshot.Index, targetMapUniqueId)
            .Where(thing => RaidSettlementBattlefieldResiduePolicy.IsBattlefieldResidue(thing.Class, thing.Def))
            .Select(thing => RaidSettlementBattlefieldResiduePolicy.ResidueKey(thing.Def, thing.Position))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);

        XDocument? returnedDocument = TryReadSnapshotDocument(returnedRaidSnapshot);
        XElement? returnedMap = returnedDocument is null
            ? null
            : FindMap(returnedDocument, returnedMapUniqueId);
        if (returnedMap is null)
        {
            return Array.Empty<RaidSettlementBattlefieldResidue>();
        }

        var residues = new List<RaidSettlementBattlefieldResidue>();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement thing in returnedMap.Element("things")?.Elements("thing") ?? Enumerable.Empty<XElement>())
        {
            RaidSettlementBattlefieldResidue? residue = TryReadBattlefieldResidue(thing);
            if (residue is null)
            {
                continue;
            }

            string key = RaidSettlementBattlefieldResiduePolicy.ResidueKey(residue.Def, residue.Position);
            if (string.IsNullOrWhiteSpace(key)
                || existingResidueKeys.Contains(key)
                || !addedKeys.Add(key))
            {
                continue;
            }

            residues.Add(residue);
        }

        return residues;
    }

    private static RaidSettlementBattlefieldResidue? TryReadBattlefieldResidue(XElement thing)
    {
        string? defName = Text(thing, "def")?.Trim();
        string? position = Text(thing, "pos")?.Trim();
        string? className = ClassName(thing);
        if (string.IsNullOrWhiteSpace(defName)
            || string.IsNullOrWhiteSpace(position)
            || !RaidSettlementBattlefieldResiduePolicy.IsBattlefieldResidue(className, defName))
        {
            return null;
        }

        return new RaidSettlementBattlefieldResidue(
            defName!,
            position!,
            className,
            Text(thing, "thickness"),
            Text(thing, "disappearAfterTicks"),
            Text(thing, "health"),
            Text(thing, "stuff"));
    }

    private static XDocument? TryReadSnapshotDocument(SaveSnapshotPackage snapshot)
    {
        if (snapshot.Payload.Length == 0)
        {
            return null;
        }

        try
        {
            byte[] payload = SaveSnapshotPackageFileReader.DecodePayload(
                snapshot.Payload,
                snapshot.Envelope.PayloadEncoding);
            if (payload.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(payload);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static XElement? FindMap(XDocument document, string mapUniqueId)
    {
        string normalizedMapUniqueId = NormalizeMapUniqueId(mapUniqueId);
        return document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li")
            .FirstOrDefault(map => string.Equals(
                NormalizeMapUniqueId(map.Element("uniqueID")?.Value),
                normalizedMapUniqueId,
                StringComparison.Ordinal));
    }

    private static string? Text(XElement? element, string childName)
    {
        return element?.Element(childName)?.Value;
    }

    private static string? ClassName(XElement element)
    {
        return element.Attribute("Class")?.Value;
    }

    private static string NormalizeCell(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }

    private static IEnumerable<string> OriginalSettlementThingKeys(ThingSummary thing)
    {
        string? localIdKey = SettlementThingIdKey(thing.MapUniqueId, thing.LocalId);
        if (!string.IsNullOrWhiteSpace(localIdKey))
        {
            yield return localIdKey!;
        }

        string? stableKey = StableSettlementThingKey(thing);
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            yield return stableKey!;
        }
    }

    private static IEnumerable<string> ReturnedSettlementThingKeys(ThingSummary thing)
    {
        string? originalThingIdKey = SettlementThingIdKey(thing.MapUniqueId, thing.ClashOfRimOriginalThingId);
        if (!string.IsNullOrWhiteSpace(originalThingIdKey))
        {
            yield return originalThingIdKey!;
        }

        string? stableKey = StableSettlementThingKey(thing);
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            yield return stableKey!;
        }
    }

    private static string? SettlementThingIdKey(string? mapUniqueId, string? thingId)
    {
        string normalizedMapUniqueId = NormalizeMapUniqueId(mapUniqueId);
        if (string.IsNullOrWhiteSpace(normalizedMapUniqueId) || string.IsNullOrWhiteSpace(thingId))
        {
            return null;
        }

        return $"{normalizedMapUniqueId}/thing:{thingId!.Trim()}";
    }

    private static string? StableSettlementThingKey(ThingSummary thing)
    {
        if (!CanUseStableSettlementThingKey(thing))
        {
            return null;
        }

        string normalizedMapUniqueId = NormalizeMapUniqueId(thing.MapUniqueId);
        string normalizedCell = NormalizeCell(thing.Position ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedMapUniqueId)
            || string.IsNullOrWhiteSpace(thing.Def)
            || string.IsNullOrWhiteSpace(normalizedCell))
        {
            return null;
        }

        return normalizedMapUniqueId
            + "/stable:"
            + StableKeyPart(thing.Def)
            + "|pos:" + normalizedCell
            + "|stuff:" + StableKeyPart(thing.Stuff)
            + "|quality:" + StableKeyPart(thing.Quality)
            + "|class:" + StableKeyPart(thing.Class);
    }

    private static string StableKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value!.Trim();
    }

    private static bool CanUseStableSettlementThingKey(ThingSummary thing)
    {
        string className = thing.Class ?? string.Empty;
        return className.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0
            || IsPlant(thing);
    }

    private static string NormalizeMapUniqueId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }

    private static bool HasMissingIdentity(SnapshotIdentity identity)
    {
        return string.IsNullOrWhiteSpace(identity.OwnerId) ||
            string.IsNullOrWhiteSpace(identity.ColonyId) ||
            string.IsNullOrWhiteSpace(identity.SnapshotId);
    }

    private static bool IdentityEquals(SnapshotIdentity left, SnapshotIdentity right)
    {
        return string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal) &&
            string.Equals(left.ColonyId, right.ColonyId, StringComparison.Ordinal) &&
            string.Equals(left.SnapshotId, right.SnapshotId, StringComparison.Ordinal);
    }

    private static bool SameColony(SnapshotIdentity left, SnapshotIdentity right)
    {
        return string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal) &&
            string.Equals(left.ColonyId, right.ColonyId, StringComparison.Ordinal);
    }
}
