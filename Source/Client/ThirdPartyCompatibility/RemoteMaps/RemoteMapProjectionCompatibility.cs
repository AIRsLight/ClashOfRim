using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;

internal sealed class RemoteMapProjectionCompatibility
{
    private readonly List<RemoteMapProjectionReferenceRewriter> RemoteMapProjectionReferenceRewriters = new();
    private readonly List<RemoteMapProjectionSanitizer> RemoteMapProjectionSanitizers = new();
    private readonly List<RemoteMapIntegerLoadIdRewriterRegistration> RemoteMapIntegerLoadIdRewriters = new();
    private readonly List<RemoteMapAreaLoadIdProvider> RemoteMapAreaLoadIdProviders = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal RemoteMapProjectionCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterRemoteMapProjectionReferenceRewriter(RemoteMapProjectionReferenceRewriter rewriter)
    {
        Registrations.RegisterUnique(RemoteMapProjectionReferenceRewriters, rewriter);
    }

    internal void RegisterRemoteMapProjectionSanitizer(RemoteMapProjectionSanitizer sanitizer)
    {
        Registrations.RegisterUnique(RemoteMapProjectionSanitizers, sanitizer);
    }

    internal void RegisterRemoteMapIntegerLoadIdRewriter(
        string loadIdPrefix,
        Func<XElement, bool> predicate,
        Func<int> nextId)
    {
        if (string.IsNullOrWhiteSpace(loadIdPrefix) || predicate is null || nextId is null)
        {
            return;
        }

        string normalizedPrefix = loadIdPrefix.Trim();
        if (RemoteMapIntegerLoadIdRewriters.Any(registration =>
            string.Equals(registration.LoadIdPrefix, normalizedPrefix, StringComparison.Ordinal)
            && Equals(registration.Predicate, predicate)
            && Equals(registration.NextId, nextId)))
        {
            return;
        }

        if (RemoteMapIntegerLoadIdRewriters.Any(registration =>
            string.Equals(registration.LoadIdPrefix, normalizedPrefix, StringComparison.Ordinal)))
        {
            Registrations.AddRegistrationDiagnostic(
                "Warning",
                "RemoteMapLoadIdPrefixShared",
                nameof(RemoteMapIntegerLoadIdRewriterRegistration),
                normalizedPrefix,
                "Remote map load id prefix '" + normalizedPrefix + "' is registered by multiple compatibility handlers.");
        }

        var registration = new RemoteMapIntegerLoadIdRewriterRegistration(normalizedPrefix, predicate, nextId);
        Registrations.Register(RemoteMapIntegerLoadIdRewriters, registration, nameof(RemoteMapIntegerLoadIdRewriterRegistration), normalizedPrefix, Registrations.ActiveRegistrationPriority());
    }

    internal IReadOnlyList<RemoteMapIntegerLoadIdRewriterRegistration> GetRemoteMapIntegerLoadIdRewriters()
    {
        return RemoteMapIntegerLoadIdRewriters.ToList();
    }

    internal void RegisterRemoteMapAreaLoadIdProvider(RemoteMapAreaLoadIdProvider provider)
    {
        Registrations.RegisterUnique(RemoteMapAreaLoadIdProviders, provider);
    }

    internal string? BuildRemoteMapAreaLoadId(XElement area, int id)
    {
        foreach (RemoteMapAreaLoadIdProvider provider in RemoteMapAreaLoadIdProviders.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(BuildRemoteMapAreaLoadId),
                    provider,
                    () => provider(area, id),
                    out string? value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal IReadOnlyList<ILoadReferenceable> RewriteRemoteMapProjectionReferences(
        ModSnapshotPackageMetadataDto package,
        XDocument sourceDocument,
        XElement projectionElement)
    {
        if (package is null || sourceDocument is null || projectionElement is null)
        {
            return Array.Empty<ILoadReferenceable>();
        }

        var references = new List<ILoadReferenceable>();
        foreach (RemoteMapProjectionReferenceRewriter rewriter in RemoteMapProjectionReferenceRewriters.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(RewriteRemoteMapProjectionReferences),
                    rewriter,
                    () => rewriter(package, sourceDocument, projectionElement),
                    out IReadOnlyList<ILoadReferenceable>? result))
            {
                continue;
            }

            if (result is null || result.Count == 0)
            {
                continue;
            }

            references.AddRange(result.Where(reference => reference is not null));
        }

        return references
            .GroupBy(reference => reference.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    internal void SanitizeRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        foreach (RemoteMapProjectionSanitizer sanitizer in RemoteMapProjectionSanitizers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(SanitizeRemoteMapProjection),
                sanitizer,
                () => sanitizer(package, mapElement, referencePawnsElement));
        }
    }
}
