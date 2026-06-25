using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal const string BiotechPawnExchangeExtensionXenotypeDef = "xenotypeDef";
    private const string BiotechPawnExchangeExtensionCustomXenotypeName = "customXenotypeName";
    private const string BiotechPawnExchangeExtensionCustomXenotypeSha256 = "customXenotypeSha256";

    private static readonly FieldInfo? GeneCachedHasCustomXenotypeField =
        typeof(Pawn_GeneTracker).GetField("cachedHasCustomXenotype", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GeneCachedCustomXenotypeField =
        typeof(Pawn_GeneTracker).GetField("cachedCustomXenotype", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void AppendBiotechPawnExchangeExtension(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (!HasBiotechPawnExchange || pawn?.genes is null || package is null)
        {
            return;
        }

        var metadata = new Dictionary<string, string?>();
        string? xenotypeDefName = pawn.genes.Xenotype?.defName;
        if (!string.IsNullOrWhiteSpace(xenotypeDefName))
        {
            metadata[BiotechPawnExchangeExtensionXenotypeDef] = xenotypeDefName;
        }

        BiotechSavedXenotypePackageDto? customXenotypePackage = SavedXenotypePackageUtility.BuildPackage(pawn);
        if (customXenotypePackage is not null)
        {
            metadata[BiotechPawnExchangeExtensionCustomXenotypeName] = customXenotypePackage.Name;
            metadata[BiotechPawnExchangeExtensionCustomXenotypeSha256] = customXenotypePackage.XmlSha256;
        }

        string? payloadJson = customXenotypePackage is null
            ? null
            : SerializeCustomXenotypePackage(customXenotypePackage);
        if (metadata.Count == 0 && string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        package.Extensions.Add(new ModPawnExchangeExtensionPackageDto
        {
            ProviderId = BiotechCompatibilityKeys.PackageId,
            Kind = BiotechCompatibilityKeys.PawnExchange,
            Metadata = metadata,
            PayloadJson = payloadJson
        });
    }

    internal static bool TryRegisterBiotechPawnExchangePayload(ModPawnExchangePackageDto package, out string message)
    {
        message = string.Empty;
        BiotechSavedXenotypePackageDto? customXenotypePackage = ResolveBiotechCustomXenotypePackage(package);
        return customXenotypePackage is null
            || SavedXenotypePackageUtility.TryRegister(customXenotypePackage, out message);
    }

    private static BiotechSavedXenotypePackageDto? ResolveBiotechCustomXenotypePackage(ModPawnExchangePackageDto package)
    {
        if (!HasBiotechPawnExchange || package?.Extensions is null)
        {
            return null;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, BiotechCompatibilityKeys.PackageId, System.StringComparison.Ordinal)
            && string.Equals(extension.Kind, BiotechCompatibilityKeys.PawnExchange, System.StringComparison.Ordinal));
        string? payloadJson = extension?.PayloadJson;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(payloadJson));
            var serializer = new DataContractJsonSerializer(typeof(BiotechSavedXenotypePackageDto));
            return serializer.ReadObject(stream) as BiotechSavedXenotypePackageDto;
        }
        catch (System.Exception ex) when (ex is System.Runtime.Serialization.SerializationException or System.ArgumentException)
        {
            Log.Warning("[ClashOfRim] Failed to parse Biotech pawn exchange extension payload: " + ex);
            return null;
        }
    }

    private static string? ResolveBiotechPawnExchangeExtensionMetadata(
        ModPawnExchangePackageDto package,
        string metadataKey)
    {
        if (!HasBiotechPawnExchange || package?.Extensions is null)
        {
            return null;
        }

        if (!string.Equals(metadataKey, BiotechPawnExchangeExtensionXenotypeDef, System.StringComparison.Ordinal))
        {
            return null;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, BiotechCompatibilityKeys.PackageId, System.StringComparison.Ordinal)
            && string.Equals(extension.Kind, BiotechCompatibilityKeys.PawnExchange, System.StringComparison.Ordinal));
        return extension?.Metadata is not null
            && extension.Metadata.TryGetValue(BiotechPawnExchangeExtensionXenotypeDef, out string? value)
            ? value
            : null;
    }

    internal static void NormalizeBiotechPawnExchangePackage(ModPawnExchangePackageDto package)
    {
        if (!HasBiotechPawnExchange || package is null)
        {
            return;
        }

        string? xenotypeDef = ResolveBiotechPawnExchangeExtensionMetadata(package, BiotechPawnExchangeExtensionXenotypeDef);
        if (string.IsNullOrWhiteSpace(xenotypeDef))
        {
            return;
        }

        ModPawnExchangeExtensionPackageDto? extension = package.Extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, BiotechCompatibilityKeys.PackageId, System.StringComparison.Ordinal)
            && string.Equals(extension.Kind, BiotechCompatibilityKeys.PawnExchange, System.StringComparison.Ordinal));
        if (extension is null)
        {
            extension = new ModPawnExchangeExtensionPackageDto
            {
                ProviderId = BiotechCompatibilityKeys.PackageId,
                Kind = BiotechCompatibilityKeys.PawnExchange,
                Metadata = new Dictionary<string, string?>()
            };
            package.Extensions.Add(extension);
        }

        extension.Metadata ??= new Dictionary<string, string?>();
        extension.Metadata[BiotechPawnExchangeExtensionXenotypeDef] = xenotypeDef;
    }

    internal static void LocalizePawnGenes(Pawn pawn)
    {
        if (!HasBiotechPawnExchange || pawn?.genes is null || Find.UniqueIDsManager is null)
        {
            return;
        }

        LocalizeGeneList(pawn.genes.Xenogenes);
        LocalizeGeneList(pawn.genes.Endogenes);
        GeneCachedHasCustomXenotypeField?.SetValue(pawn.genes, null);
        GeneCachedCustomXenotypeField?.SetValue(pawn.genes, null);
    }

    private static void LocalizeGeneList(List<Gene>? genes)
    {
        if (genes is null)
        {
            return;
        }

        for (int i = 0; i < genes.Count; i++)
        {
            Gene gene = genes[i];
            gene.loadID = Find.UniqueIDsManager.GetNextGeneID();
        }
    }

    private static string SerializeCustomXenotypePackage(BiotechSavedXenotypePackageDto package)
    {
        using var stream = new System.IO.MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(BiotechSavedXenotypePackageDto));
        serializer.WriteObject(stream, package);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
