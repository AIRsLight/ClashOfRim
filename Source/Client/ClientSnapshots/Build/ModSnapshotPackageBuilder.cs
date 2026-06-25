using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public static class ModSnapshotPackageBuilder
{
    public const string CurrentPackageVersion = "clash-of-rim-snapshot-v1";
    public const string PayloadEncoding = "GzipRws";

    public static ModSnapshotPackageBuildResult FromSaveBytes(
        byte[] originalPayload,
        string sourceLabel,
        string ownerId,
        string colonyId,
        DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(colonyId))
        {
            return ModSnapshotPackageBuildResult.Failed(
                "MissingIdentity",
                ClashOfRimText.Key("ClashOfRim.SnapshotPackage.MissingIdentity"));
        }

        if (originalPayload is null || originalPayload.Length == 0)
        {
            return ModSnapshotPackageBuildResult.Failed(
                "MissingSavePayload",
                ClashOfRimText.Key("ClashOfRim.SnapshotPackage.MissingSavePayload"));
        }

        try
        {
            string originalSha256 = Sha256Hex(originalPayload);
            byte[] encodedPayload = Gzip(originalPayload);
            string payloadSha256 = Sha256Hex(encodedPayload);
            string snapshotId = BuildSnapshotId(colonyId, utcNow, originalSha256);
            string? gameVersion = ReadGameVersion(originalPayload);
            long? gameTicks = ReadGameTicks(originalPayload);
            SnapshotLineageMarker lineage = ReadLineageMarker(originalPayload);

            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                return ModSnapshotPackageBuildResult.Failed(
                    "MissingRimWorldVersion",
                    ClashOfRimText.Key("ClashOfRim.SnapshotPackage.MissingRimWorldVersion"));
            }

            var package = new ModSnapshotPackageMetadataDto
            {
                PackageVersion = CurrentPackageVersion,
                OwnerId = ownerId,
                ColonyId = colonyId,
                SnapshotId = snapshotId,
                RimWorldVersion = gameVersion,
                PayloadEncoding = PayloadEncoding,
                OriginalSaveBytes = originalPayload.LongLength,
                PayloadBytes = encodedPayload.LongLength,
                OriginalSha256 = originalSha256,
                PayloadSha256 = payloadSha256,
                PreviousSnapshotId = lineage.SnapshotId,
                LineageToken = lineage.Token,
                GameTicks = gameTicks
            };

            return ModSnapshotPackageBuildResult.Ok(package, encodedPayload, originalPayload, sourceLabel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            return ModSnapshotPackageBuildResult.Failed(ex.GetType().Name, ex.Message);
        }
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return target.ToArray();
    }

    private static string Sha256Hex(byte[] payload)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(payload);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string BuildSnapshotId(string colonyId, DateTime utcNow, string originalSha256)
    {
        string safeColonyId = SanitizeSnapshotPart(colonyId);
        string hashPart = originalSha256.Length <= 12 ? originalSha256 : originalSha256.Substring(0, 12);
        return $"{safeColonyId}-{utcNow:yyyyMMddHHmmss}-{hashPart}";
    }

    private static string SanitizeSnapshotPart(string value)
    {
        char[] chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    private static string? ReadGameVersion(byte[] saveBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var source = new MemoryStream(saveBytes);
        using XmlReader reader = XmlReader.Create(source, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "gameVersion")
            {
                return reader.ReadElementContentAsString().Trim();
            }
        }

        return null;
    }

    private static SnapshotLineageMarker ReadLineageMarker(byte[] saveBytes)
    {
        using var source = new MemoryStream(saveBytes);
        XDocument document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        return ReadLineageMarker(document);
    }

    private static SnapshotLineageMarker ReadLineageMarker(XDocument document)
    {
        XElement? component = document.Root?
            .Element("game")?
            .Element("components")?
            .Elements("li")
            .FirstOrDefault(item => IsClashOfRimGameComponentClass(item.Attribute("Class")?.Value));
        string? snapshotId = component?.Element("clashOfRimLineageSnapshotId")?.Value.Trim();
        string? token = component?.Element("clashOfRimLineageToken")?.Value.Trim();

        return new SnapshotLineageMarker(
            string.IsNullOrWhiteSpace(snapshotId) ? null : snapshotId,
            string.IsNullOrWhiteSpace(token) ? null : token);
    }

    private static long? ReadGameTicks(byte[] saveBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var source = new MemoryStream(saveBytes);
        using XmlReader reader = XmlReader.Create(source, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.Name == "ticksGame"
                && long.TryParse(reader.ReadElementContentAsString().Trim(), out long ticks))
            {
                return ticks;
            }
        }

        return null;
    }

    private static bool IsClashOfRimGameComponentClass(string? className)
    {
        return className != null
            && className.Trim().Length > 0
            && className.IndexOf("AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal) >= 0;
    }

    private sealed class SnapshotLineageMarker
    {
        public SnapshotLineageMarker(string? snapshotId, string? token)
        {
            SnapshotId = snapshotId;
            Token = token;
        }

        public string? SnapshotId { get; }

        public string? Token { get; }
    }
}
