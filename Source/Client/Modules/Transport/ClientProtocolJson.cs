using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientProtocolJson
{
    private static readonly DataContractJsonSerializerSettings Settings = new() { UseSimpleDictionaryFormat = true };

    internal static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), Settings);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static T Deserialize<T>(string json, Func<string, string> invalidResponseMessage)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), Settings);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return serializer.ReadObject(stream) is T value ? value
            : throw new InvalidOperationException(invalidResponseMessage(typeof(T).Name));
    }
}
