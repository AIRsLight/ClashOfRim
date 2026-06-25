using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteMapProjectedLoadIdRewriter
{
    private static readonly FieldInfo? LoadedObjectDirectoryField =
        typeof(CrossRefHandler).GetField("loadedObjectDirectory", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? AllObjectsByLoadIdField =
        typeof(LoadedObjectDirectory).GetField("allObjectsByLoadID", BindingFlags.Instance | BindingFlags.NonPublic);

    public static IReadOnlyList<RemoteMapProjectedThingIdentity> Rewrite(XElement mapElement, XElement referencePawnsElement)
    {
        RemoteMapProjectedIdAllocator allocator = RemoteMapProjectedIdAllocator.Create(mapElement, referencePawnsElement);
        var replacements = new Dictionary<string, string>(System.StringComparer.Ordinal);
        var projectedThingIdentities = new List<RemoteMapProjectedThingIdentity>();
        int things = RewriteThingIds(mapElement, replacements, allocator, projectedThingIdentities) + RewriteThingIds(referencePawnsElement, replacements, allocator);
        int areas = RewriteAreaIds(mapElement, replacements, allocator);
        int compatibilityIds = RewriteCompatibilityIntegerLoadIds(mapElement, referencePawnsElement, replacements, allocator);
        int hediffs = RewriteIntegerLoadIds(mapElement, referencePawnsElement, "Hediff_", IsHediffNode, () => Find.UniqueIDsManager.GetNextHediffID(), replacements, allocator);
        int jobs = RewriteIntegerLoadIds(mapElement, referencePawnsElement, "Job_", IsJobNode, () => Find.UniqueIDsManager.GetNextJobID(), replacements, allocator);
        int abilities = RewriteAbilityIds(mapElement, replacements, allocator) + RewriteAbilityIds(referencePawnsElement, replacements, allocator);
        IReadOnlyList<KeyValuePair<string, string>> embeddedReferenceReplacements = BuildEmbeddedReferenceReplacements(replacements);
        int verbs = RewriteVerbLoadIds(mapElement, replacements, embeddedReferenceReplacements) + RewriteVerbLoadIds(referencePawnsElement, replacements, embeddedReferenceReplacements);

        if (replacements.Count > 0)
        {
            RewriteReferences(mapElement, replacements);
            RewriteReferences(referencePawnsElement, replacements);
        }

        if (things > 0 || areas > 0 || compatibilityIds > 0 || hediffs > 0 || jobs > 0 || abilities > 0 || verbs > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Rewrote projected load IDs: things="
                + things
                + ", areas="
                + areas
                + ", compatibility="
                + compatibilityIds
                + ", hediffs="
                + hediffs
                + ", jobs="
                + jobs
                + ", abilities="
                + abilities
                + ", verbs="
                + verbs
                + ".");
        }

        return projectedThingIdentities;
    }

    private static int RewriteThingIds(
        XElement root,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator,
        ICollection<RemoteMapProjectedThingIdentity>? projectedThingIdentities = null)
    {
        int count = 0;
        foreach (XElement thing in root.Descendants()
                     .Where(element => element.Element("def") is not null && element.Element("id") is not null)
                     .ToList())
        {
            XElement? idElement = thing.Element("id");
            string oldThingId = idElement?.Value.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(oldThingId))
            {
                continue;
            }

            string prefix = StripTrailingDigits(oldThingId);
            if (prefix.Length == oldThingId.Length)
            {
                continue;
            }

            string newThingId = prefix + allocator.NextThingId();
            AddReplacement(replacements, oldThingId, newThingId);
            AddReplacement(replacements, "Thing_" + oldThingId, "Thing_" + newThingId);
            SetElement(thing, "clashOfRimOriginalThingId", oldThingId);
            SetElement(thing, "clashOfRimProjectedThingId", newThingId);
            idElement!.Value = newThingId;
            projectedThingIdentities?.Add(new RemoteMapProjectedThingIdentity(newThingId, oldThingId));
            count++;
        }

        return count;
    }

    private static int RewriteCompatibilityIntegerLoadIds(
        XElement mapElement,
        XElement referencePawnsElement,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator)
    {
        int count = 0;
        foreach (RemoteMapIntegerLoadIdRewriterRegistration registration in ClashOfRimCompatibilityApi.GetRemoteMapIntegerLoadIdRewriters())
        {
            count += RewriteIntegerLoadIds(
                mapElement,
                referencePawnsElement,
                registration.LoadIdPrefix,
                registration.Predicate,
                registration.NextId,
                replacements,
                allocator);
        }

        return count;
    }

    private static int RewriteAreaIds(
        XElement mapElement,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator)
    {
        XElement? areasRoot = mapElement.Descendants("areas").FirstOrDefault();
        if (areasRoot is null)
        {
            return 0;
        }

        int count = 0;
        foreach (XElement area in areasRoot.Elements("li").ToList())
        {
            XElement? idElement = area.Element("ID");
            if (idElement is null || !int.TryParse(idElement.Value.Trim(), out int oldId))
            {
                continue;
            }

            string? oldLoadId = BuildAreaLoadId(area, oldId);
            int newId = allocator.NextLoadId("Area_", () => Find.UniqueIDsManager.GetNextAreaID());
            idElement.Value = newId.ToString();
            string? newLoadId = BuildAreaLoadId(area, newId);
            if (!string.IsNullOrWhiteSpace(oldLoadId) && !string.IsNullOrWhiteSpace(newLoadId))
            {
                AddReplacement(replacements, oldLoadId!, newLoadId!);
            }

            count++;
        }

        return count;
    }

    private static int RewriteIntegerLoadIds(
        XElement mapElement,
        XElement referencePawnsElement,
        string loadIdPrefix,
        System.Func<XElement, bool> predicate,
        System.Func<int> nextId,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator)
    {
        int count = 0;
        count += RewriteIntegerLoadIds(mapElement, loadIdPrefix, predicate, nextId, replacements, allocator);
        count += RewriteIntegerLoadIds(referencePawnsElement, loadIdPrefix, predicate, nextId, replacements, allocator);
        return count;
    }

    private static int RewriteIntegerLoadIds(
        XElement root,
        string loadIdPrefix,
        System.Func<XElement, bool> predicate,
        System.Func<int> nextId,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator)
    {
        int count = 0;
        foreach (XElement node in root.Descendants().Where(predicate).ToList())
        {
            XElement? loadId = node.Element("loadID");
            if (loadId is null || !int.TryParse(loadId.Value.Trim(), out int oldId))
            {
                int createdId = allocator.NextLoadId(loadIdPrefix, nextId);
                node.AddFirst(new XElement("loadID", createdId.ToString()));
                count++;
                continue;
            }

            int newId = allocator.NextLoadId(loadIdPrefix, nextId);
            loadId.Value = newId.ToString();
            AddReplacement(replacements, loadIdPrefix + oldId, loadIdPrefix + newId);
            count++;
        }

        return count;
    }

    private static int RewriteAbilityIds(
        XElement root,
        IDictionary<string, string> replacements,
        RemoteMapProjectedIdAllocator allocator)
    {
        int count = 0;
        foreach (XElement node in root.Descendants().Where(IsAbilityNode).ToList())
        {
            XElement? id = node.Element("Id");
            if (id is null || !int.TryParse(id.Value.Trim(), out int oldId))
            {
                continue;
            }

            string oldLoadId = "Ability_" + oldId;
            int newId = allocator.NextLoadId("Ability_", () => Find.UniqueIDsManager.GetNextAbilityID());
            string newLoadId = "Ability_" + newId;
            id.Value = newId.ToString();
            RewriteEmbeddedReferences(node, oldLoadId, newLoadId);
            AddReplacement(replacements, oldLoadId, newLoadId);
            count++;
        }

        return count;
    }

    private static int RewriteVerbLoadIds(
        XElement root,
        IDictionary<string, string> replacements,
        IReadOnlyList<KeyValuePair<string, string>> embeddedReferenceReplacements)
    {
        int count = 0;
        foreach (XElement node in root.Descendants().Where(IsVerbNode).ToList())
        {
            XElement? loadId = node.Element("loadID");
            string oldId = loadId?.Value.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(oldId))
            {
                continue;
            }

            string newId = RewriteEmbeddedLoadId(oldId, embeddedReferenceReplacements);
            if (string.Equals(oldId, newId, StringComparison.Ordinal))
            {
                continue;
            }

            loadId!.Value = newId;
            AddReplacement(replacements, "Verb_" + oldId, "Verb_" + newId);
            count++;
        }

        return count;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildEmbeddedReferenceReplacements(IDictionary<string, string> replacements)
    {
        return replacements
            .Where(pair => !pair.Key.StartsWith("Verb_", StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length)
            .ToList();
    }

    private static string RewriteEmbeddedLoadId(string loadId, IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        string rewritten = loadId;
        foreach (KeyValuePair<string, string> replacement in replacements)
        {
            if (rewritten.IndexOf(replacement.Key, StringComparison.Ordinal) >= 0)
            {
                rewritten = rewritten.Replace(replacement.Key, replacement.Value);
            }
        }

        return rewritten;
    }

    private static void RewriteReferences(XElement root, IReadOnlyDictionary<string, string> replacements)
    {
        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (IsProjectionMetadataElement(element))
            {
                continue;
            }

            if (!element.HasElements)
            {
                string value = element.Value.Trim();
                if (replacements.TryGetValue(value, out string? replacement))
                {
                    element.Value = replacement;
                }
            }
        }
    }

    private static bool IsProjectionMetadataElement(XElement element)
    {
        string name = element.Name.LocalName;
        return string.Equals(name, "clashOfRimOriginalThingId", StringComparison.Ordinal)
            || string.Equals(name, "clashOfRimProjectedThingId", StringComparison.Ordinal);
    }

    private static void RewriteEmbeddedReferences(XElement root, string oldValue, string newValue)
    {
        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (!element.HasElements)
            {
                string value = element.Value;
                if (value.IndexOf(oldValue, StringComparison.Ordinal) >= 0)
                {
                    element.Value = value.Replace(oldValue, newValue);
                }
            }
        }
    }

    private static bool IsHediffNode(XElement element)
    {
        if (element.Element("def") is null)
        {
            return false;
        }

        string className = element.Attribute("Class")?.Value ?? string.Empty;
        string nodeName = element.Name.LocalName;
        bool isHediffClass = className.IndexOf("Hediff", StringComparison.Ordinal) >= 0;
        bool isHediffListItem = string.Equals(nodeName, "li", StringComparison.OrdinalIgnoreCase)
            && HasAncestorNamed(element, "hediffs");
        return isHediffClass || isHediffListItem;
    }

    private static bool IsJobNode(XElement element)
    {
        if (element.Element("def") is null)
        {
            return false;
        }

        string className = element.Attribute("Class")?.Value ?? string.Empty;
        string nodeName = element.Name.LocalName;
        bool isJobClass = className.IndexOf("Job", StringComparison.Ordinal) >= 0;
        bool isJobListItem = string.Equals(nodeName, "li", StringComparison.OrdinalIgnoreCase)
            && (HasAncestorNamed(element, "jobs") || HasAncestorNamed(element, "jobQueue"));
        bool isNamedJobNode = string.Equals(nodeName, "curJob", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "job", StringComparison.OrdinalIgnoreCase);
        return isJobClass || isJobListItem || isNamedJobNode;
    }

    private static bool IsGeneNode(XElement element)
    {
        if (element.Element("def") is null)
        {
            return false;
        }

        string className = element.Attribute("Class")?.Value ?? string.Empty;
        string nodeName = element.Name.LocalName;
        bool isGeneClass = className.IndexOf("Gene", StringComparison.Ordinal) >= 0;
        bool isGeneListItem = string.Equals(nodeName, "li", StringComparison.OrdinalIgnoreCase)
            && (HasAncestorNamed(element, "endogenes") || HasAncestorNamed(element, "xenogenes"));
        return isGeneClass || isGeneListItem;
    }

    private static bool IsAbilityNode(XElement element)
    {
        string className = element.Attribute("Class")?.Value ?? string.Empty;
        string nodeName = element.Name.LocalName;
        return element.Element("Id") is not null
            && (className.Contains("Ability")
                || string.Equals(nodeName, "ability", System.StringComparison.OrdinalIgnoreCase)
                || HasAncestorNamed(element, "abilities"));
    }

    private static bool IsVerbNode(XElement element)
    {
        string className = element.Attribute("Class")?.Value ?? string.Empty;
        return element.Element("loadID") is not null
            && (className.Contains("Verb") || HasAncestorNamed(element, "verbs") || HasAncestorNamed(element, "verbTracker"));
    }

    private static bool HasAncestorNamed(XElement element, string name)
    {
        return element.Ancestors().Any(ancestor => string.Equals(ancestor.Name.LocalName, name, System.StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildAreaLoadId(XElement area, int id)
    {
        string className = area.Attribute("Class")?.Value ?? string.Empty;
        if (className.Contains("Area_Home"))
        {
            return "Area_" + id + "_Home";
        }

        if (className.Contains("Area_BuildRoof"))
        {
            return "Area_" + id + "_BuildRoof";
        }

        if (className.Contains("Area_NoRoof"))
        {
            return "Area_" + id + "_NoRoof";
        }

        if (className.Contains("Area_SnowOrSandClear"))
        {
            return "Area_" + id + "_SnowClear";
        }

        if (className.Contains("Area_Allowed"))
        {
            string label = area.Element("label")?.Value
                ?? area.Element("labelInt")?.Value
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(label))
            {
                return "Area_" + id + "_Named_" + label;
            }
        }

        return ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(area, id);
    }

    private static string StripTrailingDigits(string value)
    {
        int index = value.Length - 1;
        while (index >= 0 && char.IsDigit(value[index]))
        {
            index--;
        }

        return value.Substring(0, index + 1);
    }

    private static void AddReplacement(IDictionary<string, string> replacements, string oldValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(oldValue)
            || string.IsNullOrWhiteSpace(newValue)
            || string.Equals(oldValue, newValue, System.StringComparison.Ordinal))
        {
            return;
        }

        if (!replacements.ContainsKey(oldValue))
        {
            replacements[oldValue] = newValue;
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

    private sealed class RemoteMapProjectedIdAllocator
    {
        private readonly HashSet<int> usedThingIds = new();
        private readonly Dictionary<string, HashSet<int>> usedLoadIdsByPrefix = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> highWaterByPrefix = new(StringComparer.Ordinal);

        private RemoteMapProjectedIdAllocator()
        {
        }

        public static RemoteMapProjectedIdAllocator Create(params XElement[] roots)
        {
            var allocator = new RemoteMapProjectedIdAllocator();
            allocator.ReserveCurrentGameIds();
            foreach (XElement root in roots.Where(root => root is not null))
            {
                allocator.ReserveXmlIds(root);
            }

            return allocator;
        }

        public int NextThingId()
        {
            return NextUnique(usedThingIds, () => Find.UniqueIDsManager.GetNextThingID());
        }

        public int NextLoadId(string loadIdPrefix, Func<int> nextId)
        {
            if (string.IsNullOrWhiteSpace(loadIdPrefix))
            {
                return nextId();
            }

            return NextUnique(UsedLoadIds(loadIdPrefix), () =>
            {
                int candidate = nextId();
                int highWater = highWaterByPrefix.TryGetValue(loadIdPrefix, out int value) ? value : -1;
                if (candidate <= highWater)
                {
                    candidate = highWater + 1;
                }

                highWaterByPrefix[loadIdPrefix] = candidate;
                return candidate;
            });
        }

        private static int NextUnique(HashSet<int> used, Func<int> nextId)
        {
            int guard = 0;
            while (true)
            {
                int candidate = nextId();
                if (candidate >= 0 && used.Add(candidate))
                {
                    return candidate;
                }

                guard++;
                if (guard > 1000000)
                {
                    throw new InvalidOperationException("Unable to allocate a unique projected remote map load id.");
                }
            }
        }

        private void ReserveCurrentGameIds()
        {
            ReserveLoadedObjectDirectoryIds();

            if (Current.Game?.Maps is not null)
            {
                foreach (Map map in Current.Game.Maps)
                {
                    foreach (Thing thing in map.listerThings?.AllThings ?? Enumerable.Empty<Thing>())
                    {
                        ReserveThingTree(thing);
                    }
                }
            }

            if (Find.WorldPawns is not null)
            {
                foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
                {
                    ReserveThingTree(pawn);
                }
            }
        }

        private void ReserveLoadedObjectDirectoryIds()
        {
            if (LoadedObjectDirectoryField?.GetValue(Scribe.loader?.crossRefs) is not LoadedObjectDirectory directory
                || AllObjectsByLoadIdField?.GetValue(directory) is not System.Collections.IDictionary loadedObjects)
            {
                return;
            }

            foreach (object? key in loadedObjects.Keys)
            {
                if (key is string loadId)
                {
                    ReserveEmbeddedLoadId(loadId);
                }
            }
        }

        private void ReserveThingTree(Thing? thing)
        {
            if (thing is null)
            {
                return;
            }

            if (thing.def?.HasThingIDNumber == true && thing.thingIDNumber >= 0)
            {
                usedThingIds.Add(thing.thingIDNumber);
            }

            if (thing is Pawn pawn)
            {
                ReservePawnIds(pawn);
            }
        }

        private void ReservePawnIds(Pawn pawn)
        {
            if (pawn.health?.hediffSet?.hediffs is { } hediffs)
            {
                foreach (Hediff hediff in hediffs)
                {
                    ReserveLoadId("Hediff_", hediff.loadID);
                    if (hediff.AllAbilitiesForReading is { } hediffAbilities)
                    {
                        foreach (Ability ability in hediffAbilities)
                        {
                            ReserveAbility(ability);
                        }
                    }
                }
            }

            if (pawn.abilities?.AllAbilitiesForReading is { } abilities)
            {
                foreach (Ability ability in abilities)
                {
                    ReserveAbility(ability);
                }
            }

            if (pawn.equipment?.AllEquipmentListForReading is { } equipment)
            {
                foreach (Thing thing in equipment)
                {
                    ReserveThingTree(thing);
                }
            }

            if (pawn.apparel?.WornApparel is { } apparel)
            {
                foreach (Apparel worn in apparel)
                {
                    ReserveThingTree(worn);
                    foreach (Ability ability in worn.AllAbilitiesForReading)
                    {
                        ReserveAbility(ability);
                    }
                }
            }

            if (pawn.inventory?.innerContainer is { } inventory)
            {
                foreach (Thing thing in inventory)
                {
                    ReserveThingTree(thing);
                }
            }

            if (pawn.carryTracker?.CarriedThing is { } carried)
            {
                ReserveThingTree(carried);
            }

            if (ModsConfig.BiotechActive && pawn.genes is not null)
            {
                foreach (Gene gene in pawn.genes.Xenogenes.Concat(pawn.genes.Endogenes))
                {
                    ReserveLoadId("Gene_", gene.loadID);
                }
            }
        }

        private void ReserveAbility(Ability? ability)
        {
            if (ability is not null)
            {
                ReserveLoadId("Ability_", ability.Id);
            }
        }

        private void ReserveXmlIds(XElement root)
        {
            foreach (XElement thing in root.Descendants()
                         .Where(element => element.Element("id") is not null)
                         .ToList())
            {
                string value = thing.Element("id")?.Value.Trim() ?? string.Empty;
                if (TryReadTrailingNumber(value, out int number))
                {
                    usedThingIds.Add(number);
                }
            }

            foreach (XElement node in root.DescendantsAndSelf())
            {
                XElement? loadId = node.Element("loadID");
                if (loadId is not null
                    && int.TryParse(loadId.Value.Trim(), out int numericLoadId))
                {
                    string prefix = InferLoadIdPrefix(node);
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        ReserveLoadId(prefix, numericLoadId);
                    }
                }

                XElement? abilityId = node.Element("Id");
                if (abilityId is not null
                    && int.TryParse(abilityId.Value.Trim(), out int abilityNumericId))
                {
                    ReserveLoadId("Ability_", abilityNumericId);
                }

                string value = !node.HasElements ? node.Value.Trim() : string.Empty;
                ReserveEmbeddedLoadId(value);
            }
        }

        private string InferLoadIdPrefix(XElement node)
        {
            string className = node.Attribute("Class")?.Value ?? string.Empty;
            if (IsHediffNode(node))
            {
                return "Hediff_";
            }

            if (IsGeneNode(node))
            {
                return "Gene_";
            }

            if (IsJobNode(node))
            {
                return "Job_";
            }

            if (className.Contains("Ability") || HasAncestorNamed(node, "abilities"))
            {
                return "Ability_";
            }

            return string.Empty;
        }

        private void ReserveEmbeddedLoadId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (string prefix in new[] { "Ability_", "Hediff_", "Gene_", "Job_", "Area_" })
            {
                int start = value.IndexOf(prefix, StringComparison.Ordinal);
                if (start < 0)
                {
                    continue;
                }

                int numberStart = start + prefix.Length;
                int numberEnd = numberStart;
                while (numberEnd < value.Length && char.IsDigit(value[numberEnd]))
                {
                    numberEnd++;
                }

                if (numberEnd > numberStart
                    && int.TryParse(value.Substring(numberStart, numberEnd - numberStart), out int number))
                {
                    ReserveLoadId(prefix, number);
                }
            }
        }

        private void ReserveLoadId(string prefix, int id)
        {
            if (id >= 0)
            {
                UsedLoadIds(prefix).Add(id);
                if (!highWaterByPrefix.TryGetValue(prefix, out int highWater) || id > highWater)
                {
                    highWaterByPrefix[prefix] = id;
                }
            }
        }

        private HashSet<int> UsedLoadIds(string prefix)
        {
            if (!usedLoadIdsByPrefix.TryGetValue(prefix, out HashSet<int>? used))
            {
                used = new HashSet<int>();
                usedLoadIdsByPrefix[prefix] = used;
            }

            return used;
        }

        private static bool TryReadTrailingNumber(string value, out int number)
        {
            number = -1;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            int index = value.Length - 1;
            while (index >= 0 && char.IsDigit(value[index]))
            {
                index--;
            }

            return index < value.Length - 1
                && int.TryParse(value.Substring(index + 1), out number);
        }
    }
}
