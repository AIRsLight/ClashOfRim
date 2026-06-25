using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnScribeRestorer
{
    public static bool TryRestore(
        SupportPawnPayloadSummary payload,
        out Pawn? pawn,
        out string message)
    {
        return PawnExchangeRestoreService.TryRestore(
            ToPawnExchangePackage(payload),
            PawnExchangeRestoreKind.Support,
            out pawn,
            out message);
    }

    private static ModPawnExchangePackageDto ToPawnExchangePackage(SupportPawnPayloadSummary payload)
    {
        SupportPawnExchangePackageSummary? package = payload.PawnPackage;
        SupportPawnReferenceSummary? reference = package?.Reference ?? payload.PawnReference;
        return new ModPawnExchangePackageDto
        {
            PackageVersion = package?.PackageVersion ?? 1,
            Reference = new ModCrossMapPawnReferenceDto
            {
                GlobalId = string.IsNullOrWhiteSpace(reference?.GlobalId)
                    ? payload.PawnGlobalKey
                    : reference!.GlobalId!,
                SourceSnapshotId = string.IsNullOrWhiteSpace(reference?.SourceSnapshotId)
                    ? payload.SourceSnapshotId
                    : reference!.SourceSnapshotId,
                Name = string.IsNullOrWhiteSpace(reference?.Name)
                    ? payload.PawnName
                    : reference!.Name,
                Dead = reference?.Dead ?? false,
                Faction = reference?.Faction,
                Metadata = CopyReferenceMetadata(reference, payload.PawnReference)
            },
            Identity = new ModPawnExchangeIdentityDto
            {
                ThingDef = package?.Identity?.ThingDef,
                PawnKindDef = package?.Identity?.PawnKindDef,
                FactionDef = package?.Identity?.FactionDef,
                Gender = package?.Identity?.Gender
            },
            Appearance = new ModPawnExchangeAppearanceDto
            {
                DisplayName = package?.Appearance?.DisplayName ?? payload.PawnName
            },
            Extensions = package?.Extensions.Select(extension =>
                new ModPawnExchangeExtensionPackageDto
                {
                    ProviderId = extension.ProviderId,
                    Kind = extension.Kind,
                    Metadata = extension.Metadata,
                    PayloadJson = extension.PayloadJson
                }).ToList() ?? new System.Collections.Generic.List<ModPawnExchangeExtensionPackageDto>(),
            Relationships = package?.Relationships.Select(relationship =>
                new ModPawnExchangeRelationshipStubDto
                {
                    OtherPawnGlobalId = relationship.OtherPawnGlobalId,
                    OtherPawnName = relationship.OtherPawnName,
                    OtherPawnDead = relationship.OtherPawnDead,
                    RelationDef = relationship.RelationDef
                }).ToList() ?? new System.Collections.Generic.List<ModPawnExchangeRelationshipStubDto>(),
            Scribe = package?.Scribe is null
                ? null
                : new ModPawnScribePayloadDto
                {
                    Xml = package.Scribe.Xml,
                    XmlSha256 = package.Scribe.XmlSha256,
                    PawnReferenceReplacements = package.Scribe.PawnReferenceReplacements.Select(replacement =>
                        new ModPawnScribePawnReferenceReplacementDto
                        {
                            SourceLoadId = replacement.SourceLoadId,
                            PlaceholderLoadId = replacement.PlaceholderLoadId,
                            Reference = null
                        }).ToList()
                }
        };
    }

    private static System.Collections.Generic.Dictionary<string, string?> CopyReferenceMetadata(
        SupportPawnReferenceSummary? primary,
        SupportPawnReferenceSummary? fallback)
    {
        var metadata = new System.Collections.Generic.Dictionary<string, string?>(System.StringComparer.Ordinal);
        CopyMetadata(fallback?.Metadata, metadata);
        CopyMetadata(primary?.Metadata, metadata);
        return metadata;
    }

    private static void CopyMetadata(
        System.Collections.Generic.IReadOnlyDictionary<string, string?>? source,
        System.Collections.Generic.Dictionary<string, string?> target)
    {
        if (source is null)
        {
            return;
        }

        foreach (System.Collections.Generic.KeyValuePair<string, string?> entry in source)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                target[entry.Key] = entry.Value;
            }
        }
    }
}
