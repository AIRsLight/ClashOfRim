using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangeScribeRestorer
{
    private const int MaxScribeXmlBytes = 1024 * 1024;
    private const string RelationshipPlaceholderTagPrefix = "ClashOfRim.RelationshipPlaceholder:";
    private const string PawnGlobalIdTagPrefix = "ClashOfRim.PawnGlobalId:";

    public static void StripLocalExchangeTags(Pawn? pawn)
    {
        if (pawn?.questTags is null)
        {
            return;
        }

        pawn.questTags.RemoveAll(tag => IsExchangeTag(tag));
    }

    public static int CleanupRelationshipArtifactsForRemovedPawn(Pawn? pawn)
    {
        if (pawn?.relations?.DirectRelations is null)
        {
            return 0;
        }

        List<Pawn> candidates = pawn.relations.DirectRelations
            .Where(relation => relation?.otherPawn is not null)
            .Select(relation => relation.otherPawn)
            .Where(IsRelationshipPlaceholder)
            .Distinct()
            .ToList();
        foreach (Pawn placeholder in candidates)
        {
            RemoveAllDirectRelationsBetween(pawn, placeholder);
        }

        int discarded = 0;
        foreach (Pawn placeholder in candidates)
        {
            if (!IsReferencedByAnyKnownPawn(placeholder))
            {
                DiscardWorldPawn(placeholder);
                discarded++;
            }
        }

        return discarded;
    }

    public static bool TryRestore(
        ModPawnExchangePackageDto package,
        Func<Pawn, bool> validator,
        string validatorFailure,
        string label,
        out Pawn? pawn,
        out string message,
        bool forcePlayerFaction = true)
    {
        pawn = null;
        message = string.Empty;
        ModPawnScribePayloadDto? scribe = package.Scribe;
        if (scribe is null || string.IsNullOrWhiteSpace(scribe.Xml))
        {
            message = ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusMissingScribe", label.Named("LABEL"));
            return false;
        }

        if (Encoding.UTF8.GetByteCount(scribe.Xml) > MaxScribeXmlBytes)
        {
            message = ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeTooLarge", label.Named("LABEL"));
            return false;
        }

        if (!string.IsNullOrWhiteSpace(scribe.XmlSha256)
            && !string.Equals(ComputeSha256Hex(scribe.Xml), scribe.XmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeHashMismatch", label.Named("LABEL"));
            return false;
        }

        if (!ClashOfRimCompatibilityApi.TryRegisterPawnExchangePackageExtensions(package, out string extensionMessage))
        {
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusExtensionFailed",
                label.Named("LABEL"),
                extensionMessage.Named("MESSAGE"));
            return false;
        }

        string? tempFile = null;
        bool initialized = false;
        bool finalized = false;
        try
        {
            ClashLog.Message(
                "[ClashOfRim][PawnExchange] scribe restore begin label="
                + label
                + " global="
                + (package.Reference?.GlobalId ?? "<null>")
                + " replacements="
                + (scribe.PawnReferenceReplacements?.Count ?? 0)
                + ".");
            string wrappedXml = BuildPawnLoadDocument(scribe.Xml, scribe.PawnReferenceReplacements);
            string directory = Path.Combine(Path.GetTempPath(), "ClashOfRim", "PawnExchangeScribe");
            Directory.CreateDirectory(directory);
            tempFile = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".xml");
            File.WriteAllText(tempFile, wrappedXml, Encoding.UTF8);

            Scribe.loader.InitLoading(tempFile);
            initialized = true;
            Pawn? restored = null;
            ClashLog.Message("[ClashOfRim][PawnExchange] scribe deep look begin label=" + label + ".");
            Scribe_Deep.Look(ref restored, "pawn");
            ClashLog.Message("[ClashOfRim][PawnExchange] scribe finalize begin label=" + label + ".");
            Scribe.loader.FinalizeLoading();
            finalized = true;
            ClashLog.Message(
                "[ClashOfRim][PawnExchange] scribe restore loaded label="
                + label
                + " pawn="
                + (restored?.ThingID ?? "<null>")
                + ".");

            if (restored is null)
            {
                message = ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeReturnedNull", label.Named("LABEL"));
                return false;
            }

            if (!validator(restored))
            {
                message = validatorFailure;
                return false;
            }

            if (forcePlayerFaction && restored.Faction != Faction.OfPlayer)
            {
                restored.SetFaction(Faction.OfPlayer);
            }

            if (!TryValidateRestoredPawnRuntimeState(restored, label, out message))
            {
                return false;
            }

            PawnExchangeLoadIdUtility.LocalizeRestoredPawn(restored);
            ClashLog.Message("[ClashOfRim][PawnExchange] localized restored pawn label=" + label + " pawn=" + restored.ThingID + ".");
            string metadataMessage = ClashOfRimCompatibilityApi.RestorePawnReferenceMetadata(
                restored,
                package.Reference?.Metadata,
                label);
            if ((package.Reference?.Metadata is null || package.Reference.Metadata.Count == 0))
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] Restored "
                    + label
                    + " has no pawn reference metadata; DLC references may fall back.");
            }
            else if (string.IsNullOrWhiteSpace(metadataMessage))
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] No compatibility metadata was applied for restored "
                    + label
                    + "; metadataKeys="
                    + string.Join(",", package.Reference.Metadata.Keys));
            }
            MarkPawnGlobalId(restored, package.Reference?.GlobalId);
            int mergedPlaceholders = MergeRelationshipPlaceholdersForGlobalId(restored, package.Reference?.GlobalId);
            int relationCount = RestoreOneLayerRelationships(restored, package.Relationships);
            pawn = restored;
            string relationMessage = relationCount > 0
                ? ClashOfRimText.Key(
                    "ClashOfRim.PawnExchange.StatusRestoredWithRelations",
                    relationCount.ToString(CultureInfo.InvariantCulture).Named("COUNT"))
                : string.Empty;
            if (mergedPlaceholders > 0)
            {
                ClashLog.Message("[ClashOfRim][PawnExchange] Merged relationship placeholders into restored pawn "
                    + restored.LabelShort
                    + ": count="
                    + mergedPlaceholders
                    + ".");
            }

            string details = string.Concat(metadataMessage, relationMessage);
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRestored",
                label.Named("LABEL"),
                details.Named("DETAILS"));
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or XmlException
            or InvalidOperationException
            or ArgumentException
            or NullReferenceException)
        {
            Log.Warning(
                "[ClashOfRim][PawnExchange] scribe restore failed label="
                + label
                + " global="
                + (package.Reference?.GlobalId ?? "<null>")
                + " thingDef="
                + (package.Identity?.ThingDef ?? "<null>")
                + ": "
                + ex);
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRestoreFailed",
                label.Named("LABEL"),
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            return false;
        }
        finally
        {
            if (initialized && !finalized)
            {
                try
                {
                    Scribe.loader.ForceStop();
                }
                catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
                {
                    Log.Warning("[ClashOfRim] Failed to force-stop Scribe loader after pawn exchange restore failure: " + ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(tempFile))
            {
                TryDelete(tempFile!);
            }
        }
    }

    private static string BuildPawnLoadDocument(
        string xml,
        IReadOnlyList<ModPawnScribePawnReferenceReplacementDto>? referenceReplacements)
    {
        XDocument source = ParseScribeXml(xml);
        XElement root = source.Root ?? throw new InvalidOperationException(ClashOfRimText.Key("ClashOfRim.PawnExchange.StatusScribeRootMissing"));
        XElement pawnElement = new(root)
        {
            Name = "pawn"
        };
        RemoveNestedPawnObjects(pawnElement);
        ResetExchangePawnRuntimeState(pawnElement);
        RemoveLocalSaveOnlyReferences(pawnElement);
        RemoveExternalPawnReferenceElements(pawnElement, BuildExternalPawnLoadIdSet(referenceReplacements));
        return new XDocument(new XElement("root", pawnElement)).ToString(SaveOptions.DisableFormatting);
    }

    private static XDocument ParseScribeXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxScribeXmlBytes,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void ResetExchangePawnRuntimeState(XElement pawnElement)
    {
        ReplaceElement(
            pawnElement,
            "pather",
            new XElement("pather",
                new XElement("moving", "False")));
        ReplaceElement(
            pawnElement,
            "jobs",
            new XElement("jobs",
                new XElement("curJob", new XAttribute("IsNull", "True")),
                new XElement("curDriver", new XAttribute("IsNull", "True")),
                new XElement("jobQueue", new XElement("jobs")),
                new XElement("posture", "Standing"),
                new XElement("formingCaravanTick", "-1")));
        ReplaceElement(
            pawnElement,
            "stances",
            new XElement("stances",
                new XElement("stunner",
                    new XElement("showStunMote", "False"),
                    new XElement("adaptationTicksLeft",
                        new XElement("keys"),
                        new XElement("values"))),
                new XElement("stagger"),
                new XElement("curStance", new XAttribute("Class", "Stance_Mobile"))));
        ReplaceElement(
            pawnElement,
            "carryTracker",
            new XElement("carryTracker",
                new XElement("innerContainer",
                    new XElement("innerList"))));
        ResetMindState(pawnElement);
        ResetDrafter(pawnElement);
    }

    private static bool TryValidateRestoredPawnRuntimeState(Pawn pawn, string label, out string message)
    {
        message = string.Empty;
        try
        {
            _ = pawn.carryTracker?.GetDirectlyHeldThings();
            _ = pawn.carryTracker?.CarriedThing;
            _ = pawn.inventory?.innerContainer?.Count;
            _ = pawn.equipment?.AllEquipmentListForReading?.Count;
            _ = pawn.apparel?.WornApparel?.Count;
            return true;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            Log.Warning(
                "[ClashOfRim][PawnExchange] restored pawn runtime state invalid label="
                + label
                + " pawn="
                + pawn.ThingID
                + ": "
                + ex);
            message = ClashOfRimText.Key(
                "ClashOfRim.PawnExchange.StatusRestoreFailed",
                label.Named("LABEL"),
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE"));
            return false;
        }
    }

    private static void ResetMindState(XElement pawnElement)
    {
        XElement? mindState = pawnElement.Element("mindState");
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

    private static void ResetDrafter(XElement pawnElement)
    {
        XElement? drafter = pawnElement.Element("drafter");
        if (drafter is null)
        {
            return;
        }

        SetElement(drafter, "drafted", "False");
    }

    private static void RemoveNestedPawnObjects(XElement pawnElement)
    {
        foreach (XElement nestedPawn in pawnElement
            .Descendants()
            .Where(element => element != pawnElement && LooksLikeNestedPawnObject(element))
            .ToList())
        {
            nestedPawn.Remove();
        }
    }

    private static void RemoveLocalSaveOnlyReferences(XElement pawnElement)
    {
        foreach (XElement element in pawnElement
            .Descendants()
            .Where(element => !element.HasElements)
            .Where(element => IsLocalSaveOnlyLoadId(element.Value))
            .ToList())
        {
            element.Remove();
        }
    }

    private static void RemoveExternalPawnReferenceElements(XElement pawnElement, HashSet<string> externalLoadIds)
    {
        if (externalLoadIds.Count == 0)
        {
            return;
        }

        foreach (XElement element in pawnElement
            .Descendants()
            .Where(element => !element.HasElements)
            .Where(element => externalLoadIds.Contains((element.Value ?? string.Empty).Trim()))
            .ToList())
        {
            XElement removable = NearestListItem(element) ?? element;
            removable.Remove();
        }
    }

    private static XElement? NearestListItem(XElement element)
    {
        XElement? current = element;
        while (current is not null)
        {
            if (string.Equals(current.Name.LocalName, "li", StringComparison.Ordinal))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
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

    private static void SetElement(XElement parent, string name, string value)
    {
        XElement? element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
        }
        else
        {
            element.RemoveAttributes();
            element.RemoveNodes();
            element.Value = value;
        }
    }

    private static void SetNullElement(XElement parent, string name)
    {
        SetElement(parent, name, "null");
    }

    private static void SetDeepNullElement(XElement parent, string name)
    {
        ReplaceElement(parent, name, new XElement(name, new XAttribute("IsNull", "True")));
    }

    private static HashSet<string> BuildExternalPawnLoadIdSet(
        IReadOnlyList<ModPawnScribePawnReferenceReplacementDto>? referenceReplacements)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (referenceReplacements is null)
        {
            return result;
        }

        foreach (ModPawnScribePawnReferenceReplacementDto replacement in referenceReplacements)
        {
            AddEquivalentLoadIds(result, replacement.SourceLoadId);
            AddEquivalentLoadIds(result, replacement.PlaceholderLoadId);
        }

        return result;
    }

    private static void AddEquivalentLoadIds(HashSet<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string trimmed = value?.Trim() ?? string.Empty;
        values.Add(trimmed);
        if (trimmed.StartsWith("Thing_", StringComparison.Ordinal))
        {
            values.Add(trimmed.Substring("Thing_".Length));
        }
        else
        {
            values.Add("Thing_" + trimmed);
        }
    }

    private static int RestoreOneLayerRelationships(
        Pawn pawn,
        IReadOnlyList<ModPawnExchangeRelationshipStubDto>? relationships)
    {
        if (pawn.relations is null || relationships is null || relationships.Count == 0)
        {
            return 0;
        }

        int restored = 0;
        int reused = 0;
        int created = 0;
        foreach (ModPawnExchangeRelationshipStubDto relationship in relationships.Take(128))
        {
            if (string.IsNullOrWhiteSpace(relationship.OtherPawnGlobalId)
                || string.IsNullOrWhiteSpace(relationship.RelationDef))
            {
                continue;
            }

            PawnRelationDef? relationDef = DefDatabase<PawnRelationDef>.GetNamedSilentFail(relationship.RelationDef);
            if (relationDef is null)
            {
                continue;
            }

            Pawn otherPawn = FindOrCreateRelationshipPawn(relationship, out bool createdPlaceholder);
            if (createdPlaceholder)
            {
                created++;
            }
            else
            {
                reused++;
            }

            if (otherPawn == pawn || pawn.relations.DirectRelationExists(relationDef, otherPawn))
            {
                continue;
            }

            pawn.relations.AddDirectRelation(relationDef, otherPawn);
            restored++;
        }

        if (restored > 0 || reused > 0 || created > 0)
        {
            ClashLog.Message("[ClashOfRim][PawnExchange] Restored relationships for pawn "
                + pawn.LabelShort
                + ": restored="
                + restored
                + ", reused="
                + reused
                + ", placeholders="
                + created
                + ".");
        }

        return restored;
    }

    private static Pawn FindOrCreateRelationshipPawn(
        ModPawnExchangeRelationshipStubDto relationship,
        out bool createdPlaceholder)
    {
        Pawn? existing = FindExistingPawnByGlobalId(relationship.OtherPawnGlobalId);
        if (existing is not null)
        {
            createdPlaceholder = false;
            return existing;
        }

        createdPlaceholder = true;
        string tag = RelationshipPlaceholderTagPrefix + relationship.OtherPawnGlobalId.Trim();
        Pawn placeholder = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
        if (!string.IsNullOrWhiteSpace(relationship.OtherPawnName))
        {
            placeholder.Name = new NameSingle(relationship.OtherPawnName!.Trim());
        }

        QuestUtility.AddQuestTag(ref placeholder.questTags, tag);
        MarkPawnGlobalId(placeholder, relationship.OtherPawnGlobalId);
        if (Find.WorldPawns is not null && !Find.WorldPawns.Contains(placeholder))
        {
            Find.WorldPawns.PassToWorld(placeholder, PawnDiscardDecideMode.KeepForever);
        }

        return placeholder;
    }

    private static Pawn? FindExistingPawnByGlobalId(string globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        string globalTag = PawnGlobalIdTagPrefix + globalId.Trim();
        string placeholderTag = RelationshipPlaceholderTagPrefix + globalId.Trim();
        Pawn? tagged = EnumerateKnownPawns()
            .FirstOrDefault(pawn => pawn.questTags is not null
                && (pawn.questTags.Contains(globalTag, StringComparer.Ordinal)
                    || pawn.questTags.Contains(placeholderTag, StringComparer.Ordinal)));
        if (tagged is not null)
        {
            return tagged;
        }

        string? localThingId = ExtractGlobalPawnLocalThingId(globalId);
        if (string.IsNullOrWhiteSpace(localThingId))
        {
            return null;
        }

        return EnumerateKnownPawns()
            .FirstOrDefault(pawn => string.Equals(pawn.ThingID, localThingId, StringComparison.Ordinal)
                || string.Equals(pawn.GetUniqueLoadID(), localThingId, StringComparison.Ordinal)
                || string.Equals(pawn.GetUniqueLoadID(), "Thing_" + localThingId, StringComparison.Ordinal));
    }

    private static int MergeRelationshipPlaceholdersForGlobalId(Pawn restored, string? globalId)
    {
        if (restored is null || string.IsNullOrWhiteSpace(globalId))
        {
            return 0;
        }

        string placeholderTag = RelationshipPlaceholderTagPrefix + globalId!.Trim();
        List<Pawn> placeholders = EnumerateKnownPawns()
            .Where(pawn => pawn != restored
                && pawn.questTags is not null
                && pawn.questTags.Contains(placeholderTag, StringComparer.Ordinal))
            .Distinct()
            .ToList();
        int merged = 0;
        foreach (Pawn placeholder in placeholders)
        {
            MoveRelationsFromPlaceholder(restored, placeholder);
            if (!IsReferencedByAnyKnownPawn(placeholder))
            {
                DiscardWorldPawn(placeholder);
            }

            merged++;
        }

        return merged;
    }

    private static void MoveRelationsFromPlaceholder(Pawn restored, Pawn placeholder)
    {
        if (restored is null || placeholder is null || restored == placeholder)
        {
            return;
        }

        foreach (Pawn owner in EnumerateKnownPawns().Where(pawn => pawn != placeholder).ToList())
        {
            if (owner.relations?.DirectRelations is null)
            {
                continue;
            }

            foreach (DirectPawnRelation relation in owner.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == placeholder && relation.def is not null)
                         .ToList())
            {
                AddDirectRelationIfMissing(owner, relation.def, restored);
                owner.relations.RemoveDirectRelation(relation.def, placeholder);
            }
        }

        if (placeholder.relations?.DirectRelations is null)
        {
            return;
        }

        foreach (DirectPawnRelation relation in placeholder.relations.DirectRelations
                     .Where(relation => relation?.otherPawn is not null && relation.def is not null)
                     .ToList())
        {
            if (relation.otherPawn != restored)
            {
                AddDirectRelationIfMissing(restored, relation.def, relation.otherPawn);
            }

            placeholder.relations.RemoveDirectRelation(relation.def, relation.otherPawn);
        }
    }

    private static void AddDirectRelationIfMissing(Pawn pawn, PawnRelationDef relationDef, Pawn otherPawn)
    {
        if (pawn == otherPawn || pawn.relations is null)
        {
            return;
        }

        if (!pawn.relations.DirectRelationExists(relationDef, otherPawn))
        {
            pawn.relations.AddDirectRelation(relationDef, otherPawn);
        }
    }

    private static IEnumerable<Pawn> EnumerateKnownPawns()
    {
        var seen = new HashSet<Pawn>();
        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            foreach (Pawn pawn in map.mapPawns?.AllPawns ?? new List<Pawn>())
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }

        if (Find.WorldObjects is not null)
        {
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
            {
                foreach (Pawn pawn in caravan.PawnsListForReading ?? new List<Pawn>())
                {
                    if (pawn is not null && seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }
            }
        }

        if (Find.WorldPawns is not null)
        {
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }
    }

    private static void MarkPawnGlobalId(Pawn pawn, string? globalId)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(globalId))
        {
            return;
        }

        QuestUtility.AddQuestTag(ref pawn.questTags, PawnGlobalIdTagPrefix + globalId!.Trim());
    }

    private static bool IsRelationshipPlaceholder(Pawn? pawn)
    {
        return pawn?.questTags is not null
            && pawn.questTags.Any(tag => tag.StartsWith(RelationshipPlaceholderTagPrefix, StringComparison.Ordinal));
    }

    private static bool IsExchangeTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag)
            && (tag!.StartsWith(RelationshipPlaceholderTagPrefix, StringComparison.Ordinal)
                || tag.StartsWith(PawnGlobalIdTagPrefix, StringComparison.Ordinal));
    }

    private static void RemoveAllDirectRelationsBetween(Pawn pawn, Pawn otherPawn)
    {
        if (pawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == otherPawn && relation.def is not null)
                         .ToList())
            {
                pawn.relations.RemoveDirectRelation(relation.def, otherPawn);
            }
        }

        if (otherPawn.relations?.DirectRelations is not null)
        {
            foreach (DirectPawnRelation relation in otherPawn.relations.DirectRelations
                         .Where(relation => relation?.otherPawn == pawn && relation.def is not null)
                         .ToList())
            {
                otherPawn.relations.RemoveDirectRelation(relation.def, pawn);
            }
        }
    }

    private static bool IsReferencedByAnyKnownPawn(Pawn placeholder)
    {
        foreach (Pawn pawn in EnumerateKnownPawns())
        {
            if (pawn == placeholder || pawn.relations?.DirectRelations is null)
            {
                continue;
            }

            if (pawn.relations.DirectRelations.Any(relation => relation?.otherPawn == placeholder))
            {
                return true;
            }
        }

        return false;
    }

    private static void DiscardWorldPawn(Pawn pawn)
    {
        if (pawn.Destroyed || pawn.Discarded)
        {
            return;
        }

        if (pawn.Spawned)
        {
            pawn.DeSpawnOrDeselect(DestroyMode.Vanish);
        }

        if (Find.WorldPawns is not null)
        {
            if (Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            }
            else
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
            }

            return;
        }

        pawn.Destroy(DestroyMode.Vanish);
    }

    private static string? ExtractGlobalPawnLocalThingId(string globalId)
    {
        const string marker = "/pawn:";
        int start = globalId.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = globalId.IndexOf('/', start);
        string value = end < 0
            ? globalId.Substring(start)
            : globalId.Substring(start, end - start);
        if (value.StartsWith("Thing_", StringComparison.Ordinal))
        {
            value = value.Substring("Thing_".Length);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsLocalSaveOnlyLoadId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.StartsWith("ApparelPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("DrugPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("FoodPolicy_", StringComparison.Ordinal)
            || ClashOfRimCompatibilityApi.IsPawnExchangeLocalSaveOnlyLoadId(trimmed);
    }

    private static bool LooksLikeNestedPawnObject(XElement element)
    {
        XAttribute? classAttribute = element.Attribute("Class");
        if (classAttribute is not null
            && (string.Equals(classAttribute.Value, "Pawn", StringComparison.Ordinal)
                || classAttribute.Value.EndsWith(".Pawn", StringComparison.Ordinal)))
        {
            return true;
        }

        return element.Element("id") is not null && element.Element("kindDef") is not null;
    }

    private static string ComputeSha256Hex(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
