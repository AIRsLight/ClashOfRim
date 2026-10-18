using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;

internal static class BiotechPawnExchangePayload
{
    internal static ModPawnExchangeExtensionPackageDto? Create(string? xenotypeDefName, BiotechSavedXenotypePackageDto? customXenotype)
    {
        var metadata = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(xenotypeDefName)) metadata["xenotypeDef"] = xenotypeDefName;
        string? payloadJson = null;
        if (customXenotype is not null)
        {
            metadata["customXenotypeName"] = customXenotype.Name;
            metadata["customXenotypeSha256"] = customXenotype.XmlSha256;
            using var stream = new MemoryStream();
            new DataContractJsonSerializer(typeof(BiotechSavedXenotypePackageDto)).WriteObject(stream, customXenotype);
            payloadJson = Encoding.UTF8.GetString(stream.ToArray());
        }
        if (metadata.Count == 0 && payloadJson is null) return null;
        return new ModPawnExchangeExtensionPackageDto
        {
            ProviderId = BiotechCompatibilityKeys.PackageId,
            Kind = BiotechCompatibilityKeys.PawnExchange,
            Metadata = metadata,
            PayloadJson = payloadJson
        };
    }

    internal static BiotechSavedXenotypePackageDto? ReadCustomXenotype(ModPawnExchangePackageDto? package, Action<string> warning)
    {
        var extension = package?.Extensions?.FirstOrDefault(value =>
            string.Equals(value.ProviderId, BiotechCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(value.Kind, BiotechCompatibilityKeys.PawnExchange, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson)) return null;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(extension!.PayloadJson!));
            return new DataContractJsonSerializer(typeof(BiotechSavedXenotypePackageDto)).ReadObject(stream) as BiotechSavedXenotypePackageDto;
        }
        catch (Exception ex) when (ex is SerializationException or ArgumentException)
        {
            warning("[ClashOfRim] Failed to parse Biotech pawn exchange extension payload: " + ex);
            return null;
        }
    }
}
