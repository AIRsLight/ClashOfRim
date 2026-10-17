using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientSnapshotSaveData
{
    internal static XElement? FindComponent(XDocument document) => document.Root?
        .Element("game")?.Element("components")?.Elements("li")
        .FirstOrDefault(item => item.Attribute("Class")?.Value.IndexOf(
            "AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal) >= 0);

    internal static byte[] RemovePendingAchievementQueue(byte[] saveBytes, out string? diagnostic)
    {
        diagnostic = null;
        if (saveBytes.Length == 0)
        {
            return saveBytes;
        }

        XDocument document;
        try
        {
            using var source = new MemoryStream(saveBytes);
            using XmlReader reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            diagnostic = ex.Message;
            return saveBytes;
        }

        XElement? component = FindComponent(document);
        XElement? pending = component?.Element("clashOfRimPendingAchievementCandidates");
        if (pending is null)
        {
            return saveBytes;
        }

        pending.Remove();

        using var target = new MemoryStream();
        using (var writer = XmlWriter.Create(target, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = Encoding.UTF8,
            CloseOutput = false
        }))
        {
            document.Save(writer);
        }

        return target.ToArray();
    }

}
