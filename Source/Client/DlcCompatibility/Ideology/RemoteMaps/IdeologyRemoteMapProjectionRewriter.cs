using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class IdeologyRemoteMapProjectionRewriter
{
    internal static IReadOnlyList<ILoadReferenceable> RewriteRemoteIdeoReferences(
        ModSnapshotPackageMetadataDto package,
        XDocument sourceDocument,
        XElement projectionElement)
    {
        if (!IdeologyPawnReferenceCompatibility.HasPawnReference)
        {
            return Array.Empty<ILoadReferenceable>();
        }

        IReadOnlyDictionary<string, SourceIdeoEntry> sourceIdeos = BuildSourceIdeoIndex(sourceDocument);
        var mapped = new Dictionary<string, Ideo>(StringComparer.Ordinal);
        int rewritten = 0;
        int cleared = 0;
        int serverFallbacks = 0;
        int catalogFallbacks = 0;
        int ownerPrimaryFallbacks = 0;
        int sourcePlayerMappings = 0;
        int sourceServerMappings = 0;
        int skippedContainers = 0;
        List<string> samples = new();
        foreach (XElement element in EnumerateIdeoReferenceElements(projectionElement))
        {
            if (element.HasElements)
            {
                skippedContainers++;
                continue;
            }

            string value = element.Value.Trim();
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.Ordinal))
            {
                continue;
            }

            string? localId = TryExtractLocalIdeoId(value);
            if (string.IsNullOrWhiteSpace(localId))
            {
                element.Value = "null";
                continue;
            }

            bool pawnIdeoTracker = IsPawnIdeoTrackerReference(element);
            if (TryResolveRemoteIdeo(
                    package,
                    localId!,
                    sourceIdeos.TryGetValue(localId!, out SourceIdeoEntry sourceIdeo) ? sourceIdeo : null,
                    pawnIdeoTracker,
                    out Ideo? ideo,
                    out string? resolvedGlobalKey,
                    out bool usedServerFallback,
                    out bool usedCatalogFallback,
                    out bool usedOwnerPrimaryFallback,
                    out bool usedSourcePlayerMapping,
                    out bool usedSourceServerMapping)
                && ideo is not null)
            {
                element.Value = ideo.GetUniqueLoadID();
                mapped[ideo.GetUniqueLoadID()] = ideo;
                rewritten++;
                if (usedServerFallback)
                {
                    serverFallbacks++;
                }

                if (usedCatalogFallback)
                {
                    catalogFallbacks++;
                }

                if (usedOwnerPrimaryFallback)
                {
                    ownerPrimaryFallbacks++;
                }

                if (usedSourcePlayerMapping)
                {
                    sourcePlayerMappings++;
                }

                if (usedSourceServerMapping)
                {
                    sourceServerMappings++;
                }

                if (samples.Count < 8)
                {
                    samples.Add(
                        localId
                        + "=>"
                        + (resolvedGlobalKey ?? ideo.GetUniqueLoadID())
                        + (pawnIdeoTracker ? "/pawn" : string.Empty));
                }
            }
            else
            {
                element.Value = "null";
                cleared++;
            }
        }

        if (rewritten > 0 || cleared > 0 || skippedContainers > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Rewrote ideo references: rewritten="
                + rewritten
                + ", cleared="
                + cleared
                + ", serverFallbacks="
                + serverFallbacks
                + ", catalogFallbacks="
                + catalogFallbacks
                + ", ownerPrimaryFallbacks="
                + ownerPrimaryFallbacks
                + ", sourcePlayerMappings="
                + sourcePlayerMappings
                + ", sourceServerMappings="
                + sourceServerMappings
                + ", skippedContainers="
                + skippedContainers
                + ", mappedIdeos="
                + mapped.Count
                + ", owner="
                + package.OwnerId
                + ", colony="
                + package.ColonyId
                + ", pawnSamples="
                + (samples.Count == 0 ? "<none>" : string.Join(", ", samples)));
        }

        return mapped.Values.Cast<ILoadReferenceable>().ToList();
    }

    private static bool TryResolveRemoteIdeo(
        ModSnapshotPackageMetadataDto package,
        string localId,
        SourceIdeoEntry? sourceIdeo,
        bool pawnIdeoTracker,
        out Ideo? ideo,
        out string? globalKey,
        out bool usedServerFallback,
        out bool usedCatalogFallback,
        out bool usedOwnerPrimaryFallback,
        out bool usedSourcePlayerMapping,
        out bool usedSourceServerMapping)
    {
        globalKey = BuildIdeoGlobalKey(package.OwnerId, localId);
        usedServerFallback = false;
        usedCatalogFallback = false;
        usedOwnerPrimaryFallback = false;
        usedSourcePlayerMapping = false;
        usedSourceServerMapping = false;

        if (sourceIdeo is not null)
        {
            if (sourceIdeo.InitialPlayerIdeo)
            {
                if (RemoteIdeoCatalog.TryGetIdeo(globalKey, out ideo) && ideo is not null)
                {
                    usedSourcePlayerMapping = true;
                    return true;
                }

                if (RemoteIdeoCatalog.TryFindIdeoByLocalIdForOwner(localId, package.OwnerId, out ideo, out string? ownerCatalogKey)
                    && ideo is not null)
                {
                    globalKey = ownerCatalogKey;
                    usedCatalogFallback = true;
                    usedSourcePlayerMapping = true;
                    return true;
                }

                if (pawnIdeoTracker
                    && RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(package.OwnerId, out ideo)
                    && ideo is not null)
                {
                    globalKey = ideo.GetUniqueLoadID();
                    usedOwnerPrimaryFallback = true;
                    usedSourcePlayerMapping = true;
                    return true;
                }

                return false;
            }

            string sourceServerGlobalKey = BuildIdeoGlobalKey("server", localId);
            if (RemoteIdeoCatalog.TryGetIdeo(sourceServerGlobalKey, out ideo) && ideo is not null)
            {
                globalKey = sourceServerGlobalKey;
                usedServerFallback = true;
                usedSourceServerMapping = true;
                return true;
            }
        }

        if (RemoteIdeoCatalog.TryGetIdeo(globalKey, out ideo) && ideo is not null)
        {
            return true;
        }

        if (pawnIdeoTracker
            && RemoteIdeoCatalog.TryFindIdeoByLocalIdForOwner(localId, package.OwnerId, out ideo, out string? fallbackOwnerCatalogKey)
            && ideo is not null)
        {
            globalKey = fallbackOwnerCatalogKey;
            usedCatalogFallback = true;
            return true;
        }

        string serverGlobalKey = BuildIdeoGlobalKey("server", localId);
        if (RemoteIdeoCatalog.TryGetIdeo(serverGlobalKey, out ideo) && ideo is not null)
        {
            globalKey = serverGlobalKey;
            usedServerFallback = true;
            return true;
        }

        if (RemoteIdeoCatalog.TryFindIdeoByLocalId(localId, package.OwnerId, out ideo, out string? catalogKey)
            && ideo is not null)
        {
            globalKey = catalogKey;
            usedCatalogFallback = true;
            return true;
        }

        if (pawnIdeoTracker
            && RemoteIdeoCatalog.TryFindPrimaryIdeoForOwner(package.OwnerId, out ideo)
            && ideo is not null)
        {
            globalKey = ideo.GetUniqueLoadID();
            usedOwnerPrimaryFallback = true;
            return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, SourceIdeoEntry> BuildSourceIdeoIndex(XDocument document)
    {
        var sourceIdeos = new Dictionary<string, SourceIdeoEntry>(StringComparer.Ordinal);
        XElement? ideos = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("ideoManager")?
            .Element("ideos");
        if (ideos is null)
        {
            return sourceIdeos;
        }

        foreach (XElement ideo in ideos.Elements("li"))
        {
            string? localId = ideo.Element("id")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(localId))
            {
                continue;
            }

            bool initialPlayerIdeo = string.Equals(
                ideo.Element("initialPlayerIdeo")?.Value?.Trim(),
                "True",
                StringComparison.OrdinalIgnoreCase);
            sourceIdeos[localId!] = new SourceIdeoEntry(
                localId!,
                initialPlayerIdeo,
                ideo.Element("name")?.Value?.Trim());
        }

        if (sourceIdeos.Count > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapProjection] Source ideo index built: count="
                + sourceIdeos.Count
                + ", playerIdeos="
                + sourceIdeos.Values.Count(ideo => ideo.InitialPlayerIdeo)
                + ".");
        }

        return sourceIdeos;
    }

    private static bool IsPawnIdeoTrackerReference(XElement element)
    {
        if (!string.Equals(element.Name.LocalName, "ideo", StringComparison.Ordinal))
        {
            return HasAncestorNamed(element, "previousIdeos") && LooksLikePawnReference(element);
        }

        XElement? tracker = element.Parent;
        if (tracker is null
            || !string.Equals(tracker.Name.LocalName, "ideo", StringComparison.Ordinal)
            || tracker.Element("certainty") is null)
        {
            return false;
        }

        XElement? current = tracker.Parent;
        while (current is not null)
        {
            if (LooksLikePawnElement(current))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static IEnumerable<XElement> EnumerateIdeoReferenceElements(XElement projectionElement)
    {
        return projectionElement
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "ideo", StringComparison.Ordinal)
                || (HasAncestorNamed(element, "previousIdeos")
                    && !element.HasElements
                    && element.Value.Trim().StartsWith("Ideo_", StringComparison.Ordinal)));
    }

    private static bool LooksLikePawnReference(XElement element)
    {
        XElement? current = element.Parent;
        while (current is not null)
        {
            if (LooksLikePawnElement(current))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool LooksLikePawnElement(XElement element)
    {
        if (IsPawnElement(element))
        {
            return true;
        }

        string? def = element.Element("def")?.Value?.Trim();
        string? id = element.Element("id")?.Value?.Trim();
        return element.Element("kindDef") is not null
            && (string.Equals(def, "Human", StringComparison.Ordinal)
                || element.Element("name") is not null
                || (!string.IsNullOrWhiteSpace(id) && id!.StartsWith("Human", StringComparison.Ordinal)));
    }

    private static bool HasAncestorNamed(XElement element, string name)
    {
        return element.Ancestors().Any(ancestor => string.Equals(ancestor.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPawnElement(XElement element)
    {
        return element.Element("kindDef") is not null
            && element.Element("pather") is not null
            && element.Element("jobs") is not null;
    }

    private static string? TryExtractLocalIdeoId(string loadId)
    {
        string trimmed = loadId.Trim();
        if (trimmed.StartsWith("Ideo_", StringComparison.Ordinal))
        {
            return trimmed.Substring("Ideo_".Length);
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string BuildIdeoGlobalKey(string? ownerId, string localId)
    {
        return "owner:"
            + Segment(ownerId)
            + "/ideo:"
            + localId;
    }

    private static string Segment(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value!.Trim();
    }

    private sealed class SourceIdeoEntry
    {
        public SourceIdeoEntry(string localId, bool initialPlayerIdeo, string? name)
        {
            LocalId = localId;
            InitialPlayerIdeo = initialPlayerIdeo;
            Name = name;
        }

        public string LocalId { get; }

        public bool InitialPlayerIdeo { get; }

        public string? Name { get; }
    }
}
