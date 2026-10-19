using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Pawns;

internal enum PawnScribePayloadError { None, Missing, TooLarge, HashMismatch }

// Validated, frozen payload. XML rewriting has no access to the loaded Game,
// world registries, Scribe, or compatibility registration callbacks.
internal sealed class PawnScribeDocument
{
    internal const int MaxScribeXmlBytes = 1024 * 1024;
    private readonly string xml;
    private readonly ModPawnScribePawnReferenceReplacementDto[] referenceReplacements;

    private PawnScribeDocument(ModPawnScribePayloadDto payload)
    {
        xml = payload.Xml!;
        referenceReplacements = payload.PawnReferenceReplacements?.Select(r => new ModPawnScribePawnReferenceReplacementDto
        { SourceLoadId = r.SourceLoadId, PlaceholderLoadId = r.PlaceholderLoadId }).ToArray()
            ?? Array.Empty<ModPawnScribePawnReferenceReplacementDto>();
    }

    internal static PawnScribePayloadError Read(ModPawnScribePayloadDto? payload, out PawnScribeDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(payload?.Xml)) return PawnScribePayloadError.Missing;
        if (Encoding.UTF8.GetByteCount(payload!.Xml) > MaxScribeXmlBytes) return PawnScribePayloadError.TooLarge;
        if (!string.IsNullOrWhiteSpace(payload.XmlSha256) && !ClientSnapshotPayloadCodec.HashMatches(Encoding.UTF8.GetBytes(payload.Xml), payload.XmlSha256))
            return PawnScribePayloadError.HashMismatch;
        document = new(payload);
        return PawnScribePayloadError.None;
    }

    internal string Build(Func<string, bool> localSaveOnlyLoadId, string missingRootMessage)
    {
        XDocument source = ParseScribeXml(xml);
        XElement root = source.Root ?? throw new InvalidOperationException(missingRootMessage);
        XElement pawnElement = new(root)
        {
            Name = "pawn"
        };
        RemoveNestedPawnObjects(pawnElement);
        ResetExchangePawnRuntimeState(pawnElement);
        RemoveLocalSaveOnlyReferences(pawnElement, localSaveOnlyLoadId);
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

    private static void RemoveLocalSaveOnlyReferences(XElement pawnElement, Func<string, bool> localSaveOnlyLoadId)
    {
        foreach (XElement element in pawnElement
            .Descendants()
            .Where(element => !element.HasElements)
            .Where(element => IsLocalSaveOnlyLoadId(element.Value, localSaveOnlyLoadId))
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

    private static bool IsLocalSaveOnlyLoadId(string? value, Func<string, bool> localSaveOnlyLoadId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.StartsWith("ApparelPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("DrugPolicy_", StringComparison.Ordinal)
            || trimmed.StartsWith("FoodPolicy_", StringComparison.Ordinal)
            || localSaveOnlyLoadId(trimmed);
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

}
