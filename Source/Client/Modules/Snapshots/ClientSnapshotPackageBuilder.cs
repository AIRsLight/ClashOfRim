using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientSnapshotPackageBuilder
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
            return ModSnapshotPackageBuildResult.Rejected(SnapshotPackageError.MissingIdentity);
        }

        if (originalPayload is null || originalPayload.Length == 0)
        {
            return ModSnapshotPackageBuildResult.Rejected(SnapshotPackageError.MissingSavePayload);
        }

        try
        {
            string originalSha256 = ClientSnapshotPayloadCodec.Hash(originalPayload);
            byte[] encodedPayload = ClientSnapshotPayloadCodec.Encode(originalPayload);
            string payloadSha256 = ClientSnapshotPayloadCodec.Hash(encodedPayload);
            string snapshotId = BuildSnapshotId(colonyId, utcNow, originalSha256);
            string? gameVersion = ReadGameVersion(originalPayload);
            long? gameTicks = ReadGameTicks(originalPayload);
            SnapshotLineageMarker lineage = ReadLineageMarker(originalPayload);

            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                return ModSnapshotPackageBuildResult.Rejected(SnapshotPackageError.MissingRimWorldVersion);
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

    private static string BuildSnapshotId(string colonyId, DateTime utcNow, string originalSha256)
    {
        string safeColonyId = SanitizeSnapshotPart(colonyId);
        string hashPart = originalSha256.Length <= 12 ? originalSha256 : originalSha256.Substring(0, 12);
        return $"{safeColonyId}-{utcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{hashPart}";
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
        XElement? component = ClientSnapshotSaveData.FindComponent(document);
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
                && long.TryParse(reader.ReadElementContentAsString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
            {
                return ticks;
            }
        }

        return null;
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

internal enum SnapshotPackageError { MissingIdentity, MissingSavePayload, MissingRimWorldVersion }
