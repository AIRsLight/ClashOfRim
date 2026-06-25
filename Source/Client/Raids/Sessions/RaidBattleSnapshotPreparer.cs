using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidBattleSnapshotPreparer
{
    public static bool TryPrepareReturnedSnapshot(
        byte[] saveBytes,
        ActiveRaidBattleSession session,
        out byte[] preparedSaveBytes,
        out string failureReason)
    {
        preparedSaveBytes = Array.Empty<byte>();
        failureReason = string.Empty;

        if (saveBytes.Length == 0)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementEmptySave");
            return false;
        }

        string sourceMapId = NormalizeMapId(session.TargetMapId);
        if (string.IsNullOrWhiteSpace(sourceMapId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementMissingSourceMap");
            return false;
        }

        XDocument document;
        try
        {
            using var source = new MemoryStream(saveBytes);
            document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementXmlFailed", ex.Message.Named("MESSAGE"));
            return false;
        }

        XElement? game = document.Root?.Element("game");
        XElement? maps = game?.Element("maps");
        XElement? raidParent = FindRaidBattleParent(document, session);
        if (raidParent is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementMissingParent");
            return false;
        }

        string? parentId = raidParent.Element("ID")?.Value?.Trim();
        string parentLoadId = string.IsNullOrWhiteSpace(parentId) ? string.Empty : "WorldObject_" + parentId;
        XElement? raidMap = maps?
            .Elements("li")
            .FirstOrDefault(map => string.Equals(
                map.Element("mapInfo")?.Element("parent")?.Value?.Trim(),
                parentLoadId,
                StringComparison.Ordinal));
        if (raidMap is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementMissingMap");
            return false;
        }

        string localMapId = NormalizeMapId(raidMap.Element("uniqueID")?.Value);
        if (string.IsNullOrWhiteSpace(localMapId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Raid.StatusSettlementMissingLocalMapId");
            return false;
        }

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

        preparedSaveBytes = target.ToArray();
        ClashLog.Message("[ClashOfRim][Raid] Prepared returned raid snapshot map "
            + localMapId
            + " for target "
            + sourceMapId
            + " for event "
            + session.EventId
            + ".");
        return true;
    }

    private static XElement? FindRaidBattleParent(XDocument document, ActiveRaidBattleSession session)
    {
        return document
            .Descendants("li")
            .Where(element => string.Equals(
                element.Element("clashOfRimMode")?.Value?.Trim(),
                "RaidBattle",
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(element =>
                string.IsNullOrWhiteSpace(session.EventId)
                || string.Equals(
                    element.Element("clashOfRimRelatedEventId")?.Value?.Trim(),
                    session.EventId,
                    StringComparison.Ordinal));
    }

    private static string NormalizeMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        string trimmed = mapId!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }
}
