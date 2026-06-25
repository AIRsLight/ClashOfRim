using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class WorldConfigurationExtensionPayloadJson
{
    private static readonly DataContractJsonSerializerSettings JsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };

    public static IReadOnlyList<T> Read<T>(
        IReadOnlyList<ModWorldConfigurationExtensionDto> extensions,
        string providerId,
        string kind)
    {
        ModWorldConfigurationExtensionDto? extension = extensions.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, kind, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(extension.PayloadJson));
        if (extension?.PayloadJson is null)
        {
            return Array.Empty<T>();
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(List<T>), JsonSerializerSettings);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(extension.PayloadJson));
            return serializer.ReadObject(stream) is List<T> parsed ? parsed : Array.Empty<T>();
        }
        catch (Exception ex) when (ex is System.Runtime.Serialization.SerializationException or ArgumentException)
        {
            return Array.Empty<T>();
        }
    }

    public static string Serialize<T>(IReadOnlyList<T> payload)
    {
        var serializer = new DataContractJsonSerializer(typeof(List<T>), JsonSerializerSettings);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, payload.ToList());
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
