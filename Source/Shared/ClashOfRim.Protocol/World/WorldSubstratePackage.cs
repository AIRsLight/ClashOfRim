using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

/// <summary>
/// Immutable, pre-colony world data shared by every multiplayer client.
/// It intentionally excludes runtime world state such as pawns, quests and world objects.
/// </summary>
public sealed class WorldSubstratePackage
{
    public const int CurrentFormatVersion = 1;

    public WorldSubstratePackage(
        int persistentRandomValue,
        string gridXml,
        string featuresXml,
        string landmarksXml,
        byte[]? tileGeometryPayload = null)
    {
        PersistentRandomValue = persistentRandomValue;
        GridXml = gridXml ?? throw new ArgumentNullException(nameof(gridXml));
        FeaturesXml = featuresXml ?? throw new ArgumentNullException(nameof(featuresXml));
        LandmarksXml = landmarksXml ?? throw new ArgumentNullException(nameof(landmarksXml));
        TileGeometryPayload = tileGeometryPayload is null
            ? Array.Empty<byte>()
            : (byte[])tileGeometryPayload.Clone();
    }

    public int PersistentRandomValue { get; }

    public string GridXml { get; }

    public string FeaturesXml { get; }

    public string LandmarksXml { get; }

    public byte[] TileGeometryPayload { get; }

    public static bool TryExtract(byte[] saveBytes, out WorldSubstratePackage? package, out string? failure)
    {
        package = null;
        failure = null;
        if (saveBytes is null || saveBytes.Length == 0)
        {
            failure = "Save payload is empty.";
            return false;
        }

        try
        {
            using var input = new MemoryStream(saveBytes, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XElement? world = document.Root?.Element("game")?.Element("world");
            if (world is null)
            {
                failure = "Save payload does not contain game/world.";
                return false;
            }

            XElement? grid = world.Element("grid");
            if (grid is null || grid.Element("layers") is null)
            {
                failure = "Save payload does not contain a world grid.";
                return false;
            }

            package = new WorldSubstratePackage(
                ParseInt(world.Element("info")?.Element("persistentRandomValue")),
                Serialize(grid),
                SerializeOrEmpty(world.Element("features"), "features"),
                SerializeOrEmpty(world.Element("landmarks"), "landmarks"));
            return true;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            failure = "World substrate extraction failed: " + ex.Message;
            return false;
        }
    }

    private static int ParseInt(XElement? element)
    {
        return element is not null && int.TryParse(element.Value, out int value) ? value : 0;
    }

    private static string SerializeOrEmpty(XElement? element, string name)
    {
        return element is null ? "<" + name + " />" : Serialize(element);
    }

    private static string Serialize(XElement element)
    {
        return element.ToString(SaveOptions.DisableFormatting);
    }
}
