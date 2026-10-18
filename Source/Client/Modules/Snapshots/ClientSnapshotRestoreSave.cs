using System;
using System.IO;
using System.Text;
using System.Xml;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientSnapshotRestoreSave
{
    internal static bool TryBuild(
        ModSnapshotPackageMetadataDto? package,
        byte[] payload,
        string userId,
        string colonyId,
        out string saveName,
        out byte[] saveBytes,
        out ClientSnapshotRestoreFailure? failure)
    {
        saveName = ServerSessionSaveNames.BuildSnapshotSaveName(userId, colonyId, package?.SnapshotId);
        saveBytes = Array.Empty<byte>();
        failure = null;

        if (package is null)
        {
            failure = new(ClientSnapshotRestoreFailureKind.PackageMissing);
            return false;
        }

        if (package.PayloadBytes > 0 && payload.LongLength != package.PayloadBytes)
        {
            failure = new(ClientSnapshotRestoreFailureKind.PayloadSizeMismatch);
            return false;
        }

        if (!ClientSnapshotPayloadCodec.HashMatches(payload, package.PayloadSha256))
        {
            failure = new(ClientSnapshotRestoreFailureKind.PayloadHashMismatch);
            return false;
        }

        byte[] original;
        try
        {
            original = ClientSnapshotPayloadCodec.Decode(payload, package.PayloadEncoding);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            failure = ex is NotSupportedException
                ? new(ClientSnapshotRestoreFailureKind.UnsupportedEncoding, package.PayloadEncoding)
                : new(ClientSnapshotRestoreFailureKind.DecodeFailed, ex.Message);
            return false;
        }

        if (package.OriginalSaveBytes > 0 && original.LongLength != package.OriginalSaveBytes)
        {
            failure = new(ClientSnapshotRestoreFailureKind.OriginalSizeMismatch);
            return false;
        }

        if (!ClientSnapshotPayloadCodec.HashMatches(original, package.OriginalSha256))
        {
            failure = new(ClientSnapshotRestoreFailureKind.OriginalHashMismatch);
            return false;
        }

        if (!TryRewriteServerSessionMetadata(
                original,
                saveName,
                package.SnapshotId,
                package.NextLineageToken,
                out saveBytes,
                out failure))
        {
            return false;
        }

        return true;
    }

    private static bool TryRewriteServerSessionMetadata(
        byte[] original,
        string saveName,
        string? snapshotId,
        string? lineageToken,
        out byte[] rewritten,
        out ClientSnapshotRestoreFailure? failure)
    {
        rewritten = original;
        failure = null;

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
                failure = new(ClientSnapshotRestoreFailureKind.InvalidXml);
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
            failure = new(ClientSnapshotRestoreFailureKind.MetadataRewriteFailed, ex.Message);
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

}

internal enum ClientSnapshotRestoreFailureKind
{ PackageMissing, PayloadSizeMismatch, PayloadHashMismatch, DecodeFailed, OriginalSizeMismatch, OriginalHashMismatch, UnsupportedEncoding, InvalidXml, MetadataRewriteFailed }
internal sealed record ClientSnapshotRestoreFailure(ClientSnapshotRestoreFailureKind Kind, string? Message = null);
