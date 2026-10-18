using AIRsLight.ClashOfRim.Modules.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;

internal sealed class PawnReferenceCompatibility
{
    private readonly List<PawnReferenceMetadataCollector> PawnReferenceMetadataCollectors = new();
    private readonly List<PawnReferenceMetadataResolverRegistration> PawnReferenceMetadataResolvers = new();
    private readonly List<PawnReferenceMetadataRestorerRegistration> PawnReferenceMetadataRestorers = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal PawnReferenceCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterPawnReferenceMetadataResolver(string metadataKey, PawnReferenceMetadataResolver resolver)
    {
        if (resolver is null)
        {
            return;
        }

        var registration = new PawnReferenceMetadataResolverRegistration(metadataKey, resolver);
        Registrations.RegisterMetadataKeyedRegistration(
            PawnReferenceMetadataResolvers,
            registration,
            nameof(PawnReferenceMetadataResolverRegistration),
            registration.MetadataKey,
            "PawnReferenceMetadataResolverDuplicateRejected",
            "PawnReferenceMetadataResolverDuplicateReplaced");
    }

    internal void RegisterPawnReferenceMetadataCollector(PawnReferenceMetadataCollector collector)
    {
        Registrations.RegisterUnique(PawnReferenceMetadataCollectors, collector);
    }

    internal void CollectPawnReferenceMetadata(
        Pawn pawn,
        Dictionary<string, string?> metadata,
        string? userId,
        string? colonyId)
    {
        if (pawn is null || metadata is null)
        {
            return;
        }

        foreach (PawnReferenceMetadataCollector collector in PawnReferenceMetadataCollectors.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(CollectPawnReferenceMetadata),
                collector,
                () => collector(pawn, metadata, userId, colonyId));
        }
    }

    internal string? ResolvePawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(metadataKey))
        {
            return null;
        }

        foreach (PawnReferenceMetadataResolverRegistration registration in PawnReferenceMetadataResolvers.ToList())
        {
            if (!string.Equals(registration.MetadataKey, metadataKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(ResolvePawnReferenceMetadata),
                    registration,
                    () => registration.Resolver(pawn, metadataKey, userId, colonyId),
                    out string? result))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    internal void RegisterPawnReferenceMetadataRestorer(string metadataKey, PawnReferenceMetadataRestorer restorer)
    {
        if (restorer is null)
        {
            return;
        }

        var registration = new PawnReferenceMetadataRestorerRegistration(metadataKey, restorer);
        Registrations.RegisterMetadataKeyedRegistration(
            PawnReferenceMetadataRestorers,
            registration,
            nameof(PawnReferenceMetadataRestorerRegistration),
            registration.MetadataKey,
            "PawnReferenceMetadataRestorerDuplicateRejected",
            "PawnReferenceMetadataRestorerDuplicateReplaced");
    }

    internal string RestorePawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label)
    {
        if (string.IsNullOrWhiteSpace(metadataKey))
        {
            return string.Empty;
        }

        foreach (PawnReferenceMetadataRestorerRegistration registration in PawnReferenceMetadataRestorers.ToList())
        {
            if (!string.Equals(registration.MetadataKey, metadataKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(RestorePawnReferenceMetadata),
                    registration,
                    () => registration.Restorer(pawn, metadataKey, metadataValue, label),
                    out string result))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return string.Empty;
    }

    internal string RestorePawnReferenceMetadata(
        Pawn pawn,
        IReadOnlyDictionary<string, string?>? metadata,
        string label)
    {
        if (pawn is null || metadata is null || metadata.Count == 0)
        {
            return string.Empty;
        }

        List<string> messages = new();
        foreach (KeyValuePair<string, string?> entry in metadata)
        {
            string message = RestorePawnReferenceMetadata(pawn, entry.Key, entry.Value, label);
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        return string.Concat(messages);
    }
}
