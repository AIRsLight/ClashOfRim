using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Events;

public sealed record ThingStatePackage(
    int PackageVersion,
    string GlobalKey,
    string? DefName,
    string? Label,
    int StackCount,
    ThingScribePayload Scribe,
    string? Fingerprint = null);

public sealed record ThingScribePayload(
    string XmlGzipBase64,
    string? XmlSha256,
    int UncompressedBytes);

public sealed record ThingStatePackageReadResult(
    bool Accepted,
    ThingStatePackage? Package,
    string? Error)
{
    public static ThingStatePackageReadResult Accept(ThingStatePackage package)
    {
        return new ThingStatePackageReadResult(true, package, null);
    }

    public static ThingStatePackageReadResult Reject(string error)
    {
        return new ThingStatePackageReadResult(false, null, error);
    }
}

public static class SafeThingStatePackageSerializer
{
    public const int CurrentPackageVersion = 1;
    public const int MaxJsonBytes = 2 * 1024 * 1024;
    public const int MaxScribeXmlBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> FingerprintIgnoredElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "globalKey",
        "thingIDNumber",
        "id",
        "loadID",
        "stackCount",
        "quality",
        "hitPoints",
        "maxHitPoints",
        "forbidden",
        "position",
        "pos",
        "rot",
        "rotation",
        "questTags",
        "mapIndexOrState",
        "lastTick",
        "ticksSinceCreation"
    };

    public static string Serialize(ThingStatePackage package)
    {
        ValidatePackage(package);
        string json = JsonSerializer.Serialize(package, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
        {
            throw new InvalidOperationException("thing package json is too large");
        }

        return json;
    }

    public static ThingStatePackageReadResult Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ThingStatePackageReadResult.Reject("empty package");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
        {
            return ThingStatePackageReadResult.Reject("package json is too large");
        }

        try
        {
            ThingStatePackage? package = JsonSerializer.Deserialize<ThingStatePackage>(json, JsonOptions);
            if (package is null)
            {
                return ThingStatePackageReadResult.Reject("package type mismatch");
            }

            ValidatePackage(package);
            return ThingStatePackageReadResult.Accept(package);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            return ThingStatePackageReadResult.Reject(ex.Message);
        }
    }

    public static string DecompressXml(ThingScribePayload scribe)
    {
        if (scribe is null || string.IsNullOrWhiteSpace(scribe.XmlGzipBase64))
        {
            throw new InvalidOperationException("missing scribe payload");
        }

        byte[] compressed = Convert.FromBase64String(scribe.XmlGzipBase64);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        if (output.Length > MaxScribeXmlBytes)
        {
            throw new InvalidOperationException("scribe xml is too large");
        }

        string xml = Encoding.UTF8.GetString(output.ToArray());
        if (scribe.UncompressedBytes > 0 && scribe.UncompressedBytes != Encoding.UTF8.GetByteCount(xml))
        {
            throw new InvalidOperationException("scribe xml byte count mismatch");
        }

        if (!string.IsNullOrWhiteSpace(scribe.XmlSha256)
            && !string.Equals(ComputeSha256Hex(xml), scribe.XmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("scribe xml hash mismatch");
        }

        return xml;
    }

    public static string ComputePackageFingerprint(ThingStatePackage package)
    {
        ValidatePackage(package);
        string xml = DecompressXml(package.Scribe);
        string normalized = NormalizeXmlForFingerprint(xml);
        return ComputeSha256Hex(normalized);
    }

    private static void ValidatePackage(ThingStatePackage package)
    {
        if (package.PackageVersion != CurrentPackageVersion)
        {
            throw new InvalidOperationException("unsupported thing package version");
        }

        if (string.IsNullOrWhiteSpace(package.GlobalKey))
        {
            throw new InvalidOperationException("missing global key");
        }

        if (package.Scribe is null || string.IsNullOrWhiteSpace(package.Scribe.XmlGzipBase64))
        {
            throw new InvalidOperationException("missing scribe payload");
        }

        _ = DecompressXml(package.Scribe);
    }

    private static string NormalizeXmlForFingerprint(string xml)
    {
        try
        {
            XDocument document = XDocument.Parse(xml, LoadOptions.None);
            RemoveIgnoredElements(document.Root);
            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            return xml;
        }
    }

    private static void RemoveIgnoredElements(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        foreach (XElement child in element.Elements().ToList())
        {
            if (FingerprintIgnoredElementNames.Contains(child.Name.LocalName))
            {
                child.Remove();
                continue;
            }

            RemoveIgnoredElements(child);
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
