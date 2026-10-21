using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal static class ClientProtocolJson
{
    private static readonly DataContractJsonSerializerSettings Settings = new() { UseSimpleDictionaryFormat = true };

    internal static T Copy<T>(T value) => Deserialize<T>(Serialize(value), static type => type);

    // Preserve duplicate entries and field boundaries, ignoring only item order.
    internal static string UnorderedSignature<T>(IEnumerable<T> values)
        => Fingerprint(values.Select(Fingerprint).OrderBy(value => value, StringComparer.Ordinal).ToArray());

    private static string Fingerprint<T>(T value)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        new DataContractJsonSerializer(typeof(T), Settings).WriteObject(stream, value);
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

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
