using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public static class ServerSnapshotRestoreService
{
    public static bool TryBuildServerSessionSaveBytes(
        ModSnapshotPackageMetadataDto package,
        byte[] payload,
        string userId,
        string colonyId,
        out string saveName,
        out byte[] saveBytes,
        out string failureReason)
    {
        saveName = ServerSessionSaveNames.BuildSnapshotSaveName(userId, colonyId, package?.SnapshotId);
        saveBytes = Array.Empty<byte>();
        failureReason = string.Empty;

        if (package is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.PackageMissing");
            return false;
        }

        if (package.PayloadBytes > 0 && payload.LongLength != package.PayloadBytes)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.PayloadSizeMismatch");
            return false;
        }

        if (!HashMatches(payload, package.PayloadSha256))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.PayloadHashMismatch");
            return false;
        }

        byte[] original;
        try
        {
            original = DecodePayload(payload, package.PayloadEncoding);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.DecodeFailed", ex.Message.Named("MESSAGE"));
            return false;
        }

        if (package.OriginalSaveBytes > 0 && original.LongLength != package.OriginalSaveBytes)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.OriginalSizeMismatch");
            return false;
        }

        if (!HashMatches(original, package.OriginalSha256))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.OriginalHashMismatch");
            return false;
        }

        if (!TryRewriteServerSessionMetadata(
                original,
                saveName,
                package.SnapshotId,
                package.NextLineageToken,
                out saveBytes,
                out failureReason))
        {
            return false;
        }

        return true;
    }

    private static byte[] DecodePayload(byte[] payload, string? encoding)
    {
        if (string.Equals(encoding, "RawRws", StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        if (string.Equals(encoding, "GzipRws", StringComparison.OrdinalIgnoreCase))
        {
            using var source = new MemoryStream(payload);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            gzip.CopyTo(target);
            return target.ToArray();
        }

        throw new NotSupportedException(ClashOfRimText.Key("ClashOfRim.SnapshotRestore.UnsupportedEncoding", (encoding ?? string.Empty).Named("ENCODING")));
    }

    private static bool TryRewriteServerSessionMetadata(
        byte[] original,
        string saveName,
        string? snapshotId,
        string? lineageToken,
        out byte[] rewritten,
        out string failureReason)
    {
        rewritten = original;
        failureReason = string.Empty;

        try
        {
            var document = new XmlDocument
            {
                PreserveWhitespace = true
            };
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit
            };
            using (var source = new MemoryStream(original))
            using (XmlReader reader = XmlReader.Create(source, settings))
            {
                document.Load(reader);
            }

            XmlElement? root = document.DocumentElement;
            if (root is null)
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.InvalidXml");
                return false;
            }

            XmlElement? meta = root["meta"];
            if (meta is null)
            {
                meta = document.CreateElement("meta");
                root.PrependChild(meta);
            }

            XmlElement? fileName = meta["fileName"];
            if (fileName is null)
            {
                fileName = document.CreateElement("fileName");
                meta.AppendChild(fileName);
            }

            fileName.InnerText = saveName;
            InjectLineageMarker(document, root, snapshotId, lineageToken);

            using var target = new MemoryStream();
            var writerSettings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false
            };
            using (XmlWriter writer = XmlWriter.Create(target, writerSettings))
            {
                document.Save(writer);
            }

            rewritten = target.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.SnapshotRestore.MetadataRewriteFailed", ex.Message.Named("MESSAGE"));
            return false;
        }
    }

    private static void InjectLineageMarker(
        XmlDocument document,
        XmlElement root,
        string? snapshotId,
        string? lineageToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        XmlElement? game = root["game"];
        if (game is null)
        {
            game = document.CreateElement("game");
            root.AppendChild(game);
        }

        XmlElement? components = game["components"];
        if (components is null)
        {
            components = document.CreateElement("components");
            game.AppendChild(components);
        }

        XmlElement? component = null;
        foreach (XmlNode node in components.ChildNodes)
        {
            if (node is not XmlElement element || element.Name != "li")
            {
                continue;
            }

            string? className = element.GetAttribute("Class");
            if (!string.IsNullOrWhiteSpace(className)
                && className.Contains("AIRsLight.ClashOfRim.ClashOfRimGameComponent"))
            {
                component = element;
                break;
            }
        }

        if (component is null)
        {
            component = document.CreateElement("li");
            component.SetAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent");
            components.AppendChild(component);
        }

        SetChildText(document, component, "clashOfRimLineageSnapshotId", snapshotId!);
        SetChildText(document, component, "clashOfRimLineageToken", lineageToken ?? string.Empty);
    }

    private static void SetChildText(XmlDocument document, XmlElement parent, string name, string value)
    {
        XmlElement? child = parent[name];
        if (child is null)
        {
            child = document.CreateElement(name);
            parent.AppendChild(child);
        }

        child.InnerText = value;
    }

    private static bool HashMatches(byte[] payload, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        using SHA256 sha256 = SHA256.Create();
        string actual = ToLowerHex(sha256.ComputeHash(payload));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }
}
