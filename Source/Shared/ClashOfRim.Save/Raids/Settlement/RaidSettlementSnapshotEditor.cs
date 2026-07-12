using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public static class RaidSettlementSnapshotEditor
{
    public static SaveSnapshotPackage ApplySettlementLosses(
        SaveSnapshotPackage defenderPackage,
        RaidSettlementReturnResult settlement,
        string newSnapshotId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<IRaidSettlementSnapshotEditorExtension>? editorExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(defenderPackage);
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSnapshotId);

        if (settlement.Settlement is null || string.IsNullOrWhiteSpace(settlement.TargetMapUniqueId))
        {
            throw new InvalidOperationException("Accepted raid settlement is required before editing defender snapshot.");
        }

        byte[] originalPayload = Decode(defenderPackage.Payload, defenderPackage.Envelope.PayloadEncoding);
        XDocument document;
        using (var stream = new MemoryStream(originalPayload))
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        XElement targetMap = FindMap(document, settlement.TargetMapUniqueId!)
            ?? throw new InvalidOperationException("Target map was not found in defender snapshot.");
        XElement things = targetMap.Element("things")
            ?? throw new InvalidOperationException("Target map has no things list.");

        Dictionary<string, int> lossByLocalId = settlement.Settlement.Losses
            .Where(loss => loss.LossCount > 0 && !loss.Thing.IsPawn && !string.IsNullOrWhiteSpace(loss.Thing.LocalId))
            .GroupBy(loss => loss.Thing.LocalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(loss => loss.LossCount), StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<RaidSettlementLoss>> hitPointLossesByLocalId = settlement.Settlement.Losses
            .Where(loss => loss.LossCount <= 0
                && loss.RemainingHitPointsAfterDamage.HasValue
                && !loss.Thing.IsPawn
                && !string.IsNullOrWhiteSpace(loss.Thing.LocalId))
            .GroupBy(loss => loss.Thing.LocalId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RaidSettlementLoss>)group.ToList(),
                StringComparer.Ordinal);

        var removedThingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement thing in things.Elements("thing").ToList())
        {
            string? localId = thing.Element("id")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(localId) || !lossByLocalId.TryGetValue(localId, out int lossCount))
            {
                if (!string.IsNullOrWhiteSpace(localId)
                    && hitPointLossesByLocalId.TryGetValue(localId, out IReadOnlyList<RaidSettlementLoss>? hitPointLosses))
                {
                    ApplyHitPointLosses(thing, hitPointLosses, editorExtensions);
                }

                continue;
            }

            int stackCount = ParseStackCount(thing.Element("stackCount")?.Value);
            int remaining = stackCount - lossCount;
            if (remaining <= 0)
            {
                removedThingIds.Add(localId);
                thing.Remove();
                continue;
            }

            XElement? stackElement = thing.Element("stackCount");
            if (stackElement is null)
            {
                thing.Add(new XElement("stackCount", remaining.ToString()));
            }
            else
            {
                stackElement.Value = remaining.ToString();
            }
        }

        ApplyPostSettlementEdits(targetMap, settlement.Settlement, editorExtensions);
        CleanupReferencesToRemovedThings(targetMap, removedThingIds);
        AppendBattlefieldResidues(targetMap, things, settlement.Settlement.BattlefieldResidues);

        string nextLineageToken = Guid.NewGuid().ToString("N");
        UpdateLineageMarker(document, newSnapshotId, nextLineageToken);

        byte[] editedOriginalPayload;
        using (var stream = new MemoryStream())
        {
            document.Save(stream, SaveOptions.DisableFormatting);
            editedOriginalPayload = stream.ToArray();
        }

        byte[] editedPayload = Encode(editedOriginalPayload, defenderPackage.Envelope.PayloadEncoding);
        SnapshotIdentity identity = defenderPackage.Envelope.Identity with { SnapshotId = newSnapshotId };
        SaveSnapshotEnvelope envelope = defenderPackage.Envelope with
        {
            Identity = identity,
            CreatedAtUtc = createdAtUtc,
            SourceFileName = newSnapshotId + ".rws",
            OriginalSaveBytes = editedOriginalPayload.LongLength,
            PayloadBytes = editedPayload.LongLength,
            OriginalSha256 = Sha256Hex(editedOriginalPayload),
            PayloadSha256 = Sha256Hex(editedPayload),
            PreviousSnapshotId = defenderPackage.Envelope.Identity.SnapshotId,
            LineageToken = defenderPackage.Envelope.NextLineageToken,
            NextLineageToken = nextLineageToken
        };

        string tempPath = Path.Combine(Path.GetTempPath(), "clashofrim-settlement-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            File.WriteAllBytes(tempPath, editedOriginalPayload);
            SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(tempPath, new SaveIndexReadOptions
            {
                Identity = identity,
                Extensions = SaveIndexExtensionRegistry.Registered
            });
            return new SaveSnapshotPackage(envelope, editedPayload, index);
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

    private static void ApplyHitPointLosses(
        XElement thing,
        IReadOnlyList<RaidSettlementLoss> losses,
        IReadOnlyList<IRaidSettlementSnapshotEditorExtension>? editorExtensions)
    {
        foreach (RaidSettlementLoss loss in losses)
        {
            if (TryApplyExtensionDamage(thing, loss, editorExtensions))
            {
                continue;
            }

            int remainingHitPoints = Math.Max(1, loss.RemainingHitPointsAfterDamage!.Value);
            SetElement(thing, "health", remainingHitPoints.ToString());
        }
    }

    private static bool TryApplyExtensionDamage(
        XElement thing,
        RaidSettlementLoss loss,
        IReadOnlyList<IRaidSettlementSnapshotEditorExtension>? editorExtensions)
    {
        if (editorExtensions is null || editorExtensions.Count == 0)
        {
            return false;
        }

        foreach (IRaidSettlementSnapshotEditorExtension extension in editorExtensions)
        {
            try
            {
                if (extension.TryApplySettlementDamage(thing, loss))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw ExtensionFailure(extension, "damage application", ex);
            }
        }

        return false;
    }

    private static void ApplyPostSettlementEdits(
        XElement targetMap,
        RaidSettlementDiffResult settlement,
        IReadOnlyList<IRaidSettlementSnapshotEditorExtension>? editorExtensions)
    {
        if (editorExtensions is null || editorExtensions.Count == 0)
        {
            return;
        }

        foreach (IRaidSettlementSnapshotEditorExtension extension in editorExtensions)
        {
            try
            {
                extension.ApplyPostSettlementEdit(targetMap, settlement);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw ExtensionFailure(extension, "post-settlement edit", ex);
            }
        }
    }

    private static InvalidOperationException ExtensionFailure(
        IRaidSettlementSnapshotEditorExtension extension,
        string stage,
        Exception innerException)
    {
        return new InvalidOperationException(
            $"Raid settlement editor extension '{extension.GetType().FullName}' failed during {stage}.",
            innerException);
    }

    private static XElement? FindMap(XDocument document, string mapUniqueId)
    {
        return document.Root?
            .Element("game")?
            .Element("maps")?
            .Elements("li")
            .FirstOrDefault(map => string.Equals(map.Element("uniqueID")?.Value?.Trim(), mapUniqueId, StringComparison.Ordinal));
    }

    private static int ParseStackCount(string? value)
    {
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : 1;
    }

    private static void CleanupReferencesToRemovedThings(XElement targetMap, HashSet<string> removedThingIds)
    {
        if (removedThingIds.Count == 0)
        {
            return;
        }

        XElement? things = targetMap.Element("things");
        if (things is null)
        {
            return;
        }

        foreach (XElement pawn in things.Elements("thing").Where(IsPawnThing))
        {
            CleanupPawnJobs(pawn, removedThingIds);
            CleanupPawnDirectReferences(pawn, removedThingIds);
        }

        CleanupMapDirectReferences(targetMap, removedThingIds);
    }

    private static bool IsPawnThing(XElement thing)
    {
        return string.Equals(thing.Attribute("Class")?.Value, "Pawn", StringComparison.Ordinal)
            || string.Equals(thing.Element("def")?.Value?.Trim(), "Human", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(thing.Element("kindDef")?.Value);
    }

    private static void CleanupPawnJobs(XElement pawn, HashSet<string> removedThingIds)
    {
        XElement? jobs = pawn.Element("jobs");
        if (jobs is not null && ContainsRemovedThingReference(jobs, removedThingIds))
        {
            ReplaceElement(pawn, EmptyJobsElement());
        }
    }

    private static void CleanupPawnDirectReferences(XElement pawn, HashSet<string> removedThingIds)
    {
        XElement? ownership = pawn.Element("ownership");
        if (ownership is not null)
        {
            NullDirectReferences(
                ownership,
                removedThingIds,
                "ownedBed",
                "assignedMeditationSpot",
                "assignedGrave",
                "assignedThrone",
                "assignedDeathrestCasket");
        }

        XElement? mindState = pawn.Element("mindState");
        if (mindState is not null)
        {
            NullDirectReferences(
                mindState,
                removedThingIds,
                "meleeThreat",
                "enemyTarget",
                "knownExploder",
                "lastMannedThing",
                "droppedWeapon",
                "lastAttackedTarget",
                "breachingTarget");
        }
    }

    private static void CleanupMapDirectReferences(XElement targetMap, HashSet<string> removedThingIds)
    {
        NullDirectReferences(
            targetMap,
            removedThingIds,
            "target",
            "targetA",
            "targetB",
            "targetC",
            "globalTarget",
            "thing",
            "commTarget",
            "bill",
            "lord",
            "quest",
            "verbToUse",
            "ownedBed",
            "assignedMeditationSpot",
            "assignedGrave",
            "assignedThrone",
            "assignedDeathrestCasket",
            "meleeThreat",
            "enemyTarget",
            "knownExploder",
            "lastMannedThing",
            "droppedWeapon",
            "lastAttackedTarget",
            "breachingTarget");
    }

    private static void NullDirectReferences(XElement root, HashSet<string> removedThingIds, params string[] elementNames)
    {
        var names = new HashSet<string>(elementNames, StringComparer.Ordinal);
        foreach (XElement element in root.Descendants().Where(element => !element.HasElements && names.Contains(element.Name.LocalName)))
        {
            if (removedThingIds.Contains(element.Value.Trim()))
            {
                element.Value = "null";
            }
        }
    }

    private static bool ContainsRemovedThingReference(XElement root, HashSet<string> removedThingIds)
    {
        return root.DescendantsAndSelf()
            .Where(element => !element.HasElements)
            .Any(element => removedThingIds.Contains(element.Value.Trim()));
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

    private static void AppendBattlefieldResidues(
        XElement targetMap,
        XElement things,
        IReadOnlyList<RaidSettlementBattlefieldResidue> residues)
    {
        if (residues.Count == 0)
        {
            return;
        }

        var existingResidueKeys = new HashSet<string>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedThingIdNumbers = new HashSet<int>();
        foreach (XElement thing in things.Elements("thing"))
        {
            string? id = thing.Element("id")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                usedIds.Add(id!);
                if (TryThingIdNumber(id, out int thingIdNumber))
                {
                    usedThingIdNumbers.Add(thingIdNumber);
                }
            }

            string key = RaidSettlementBattlefieldResiduePolicy.ResidueKey(
                thing.Element("def")?.Value,
                thing.Element("pos")?.Value);
            if (!string.IsNullOrWhiteSpace(key)
                && RaidSettlementBattlefieldResiduePolicy.IsBattlefieldResidue(
                    thing.Attribute("Class")?.Value,
                    thing.Element("def")?.Value))
            {
                existingResidueKeys.Add(key);
            }
        }

        int mapIndex = targetMap.ElementsBeforeSelf("li").Count();
        foreach (RaidSettlementBattlefieldResidue residue in residues)
        {
            if (!RaidSettlementBattlefieldResiduePolicy.IsBattlefieldResidue(residue.Class, residue.Def))
            {
                continue;
            }

            string key = RaidSettlementBattlefieldResiduePolicy.ResidueKey(residue.Def, residue.Position);
            if (string.IsNullOrWhiteSpace(key) || !existingResidueKeys.Add(key))
            {
                continue;
            }

            things.Add(CreateBattlefieldResidueThing(residue, mapIndex, usedIds, usedThingIdNumbers));
        }
    }

    private static XElement CreateBattlefieldResidueThing(
        RaidSettlementBattlefieldResidue residue,
        int mapIndex,
        HashSet<string> usedIds,
        HashSet<int> usedThingIdNumbers)
    {
        string className = string.IsNullOrWhiteSpace(residue.Class) ? "Filth" : residue.Class!.Trim();
        var thing = new XElement(
            "thing",
            new XAttribute("Class", className),
            new XElement("def", residue.Def),
            new XElement("id", NextThingId(residue.Def, usedIds, usedThingIdNumbers)),
            new XElement("map", mapIndex.ToString(CultureInfo.InvariantCulture)),
            new XElement("pos", residue.Position),
            new XElement("questTags", new XAttribute("IsNull", "True")),
            new XElement("spawnedTick", "0"),
            new XElement("despawnedTick", "-1"),
            new XElement("beenRevealed", "True"));

        if (!string.IsNullOrWhiteSpace(residue.HitPoints)
            && int.TryParse(residue.HitPoints, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hitPoints)
            && hitPoints > 0)
        {
            thing.Add(new XElement("health", hitPoints.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(residue.Stuff))
        {
            thing.Add(new XElement("stuff", residue.Stuff!.Trim()));
        }

        if (IsFilthResidue(className, residue.Def))
        {
            int thickness = ParseBoundedInt(residue.Thickness, defaultValue: 1, minimum: 1, maximum: 5);
            int disappearAfterTicks = ParseBoundedInt(
                residue.DisappearAfterTicks,
                defaultValue: 900000,
                minimum: 1,
                maximum: int.MaxValue);
            if (thickness > 1)
            {
                thing.Add(new XElement("thickness", thickness.ToString(CultureInfo.InvariantCulture)));
            }

            thing.Add(new XElement("disappearAfterTicks", disappearAfterTicks.ToString(CultureInfo.InvariantCulture)));
        }

        return thing;
    }

    private static bool IsFilthResidue(string? className, string? defName)
    {
        return string.Equals(className, "Filth", StringComparison.Ordinal)
            || defName?.StartsWith("Filth_", StringComparison.Ordinal) == true
            || string.Equals(defName, "SandbagRubble", StringComparison.Ordinal);
    }

    private static int ParseBoundedInt(string? value, int defaultValue, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }

    private static string NextThingId(
        string defName,
        HashSet<string> usedIds,
        HashSet<int> usedThingIdNumbers)
    {
        string prefix = SanitizeThingIdPrefix(defName);
        int seed = usedThingIdNumbers.Count == 0
            ? 900000000
            : Math.Max(900000000, usedThingIdNumbers.Max() + 1);
        while (true)
        {
            string id = prefix + seed.ToString(CultureInfo.InvariantCulture);
            if (usedIds.Add(id) && usedThingIdNumbers.Add(seed))
            {
                return id;
            }

            seed++;
        }
    }

    private static bool TryThingIdNumber(string? thingId, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(thingId))
        {
            return false;
        }

        string trimmed = thingId.Trim();
        int suffixStart = LastNonDigitIndex(trimmed) + 1;
        return suffixStart < trimmed.Length
            && int.TryParse(trimmed.Substring(suffixStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static int LastNonDigitIndex(string value)
    {
        for (int i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(value[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string SanitizeThingIdPrefix(string defName)
    {
        string prefix = new(defName
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(prefix) ? "ClashOfRim_BattlefieldResidue" : prefix;
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
}
