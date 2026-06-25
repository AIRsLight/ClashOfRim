using AIRsLight.ClashOfRim.Save;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

public sealed class IdeologySaveIndexExtension : ISaveIndexExtension, ISaveIndexDataExtension
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public IEnumerable<ThingSummary> ReadContainedThings(
        XElement containerThing,
        ThingSummary container,
        SaveIndexReadContext context)
    {
        yield break;
    }

    public IEnumerable<SaveIndexExtensionData> ReadIndexExtensions(
        XElement? world,
        SaveIndexReadContext context)
    {
        SaveIndexExtensionData? extension = BuildExtensionData(ReadIdeoSummaries(world, context).ToList());
        if (extension is not null)
        {
            yield return extension;
        }
    }

    public static SaveIndexExtensionData? BuildExtensionData(IReadOnlyList<IdeoSummary> ideos)
    {
        if (ideos.Count == 0)
        {
            return null;
        }

        return new SaveIndexExtensionData(
            IdeologyCompatibilityKeys.PackageId,
            IdeologyCompatibilityKeys.WorldIdeoCatalog,
            "1",
            JsonSerializer.Serialize(ideos, PayloadJsonOptions),
            new Dictionary<string, string?>
            {
                ["count"] = ideos.Count.ToString(CultureInfo.InvariantCulture)
            });
    }

    public static IReadOnlyList<IdeoSummary> ReadIdeos(IReadOnlyList<SaveIndexExtensionData>? extensions)
    {
        SaveIndexExtensionData? extension = extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, IdeologyCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, IdeologyCompatibilityKeys.WorldIdeoCatalog, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
        {
            return Array.Empty<IdeoSummary>();
        }

        try
        {
            List<IdeoSummary>? parsed = JsonSerializer.Deserialize<List<IdeoSummary>>(
                extension.PayloadJson,
                PayloadJsonOptions);
            return parsed is null ? Array.Empty<IdeoSummary>() : parsed;
        }
        catch (JsonException)
        {
            return Array.Empty<IdeoSummary>();
        }
    }

    private static IEnumerable<IdeoSummary> ReadIdeoSummaries(
        XElement? world,
        SaveIndexReadContext context)
    {
        IEnumerable<XElement> ideos = world?
            .Element("ideoManager")?
            .Element("ideos")?
            .Elements("li") ?? Enumerable.Empty<XElement>();

        foreach (XElement ideo in ideos)
        {
            string? id = Text(ideo, "id");
            yield return new IdeoSummary(
                id,
                string.IsNullOrWhiteSpace(id) ? null : BuildIdeoKey(context.Identity, id),
                Text(ideo, "name"),
                Text(ideo, "culture"),
                null,
                null,
                Text(ideo, "primaryFactionColor"),
                null,
                Text(ideo.Element("foundation"), "def"),
                null,
                Text(ideo, "iconDef"),
                null,
                Text(ideo, "colorDef"),
                null,
                DefNames(ideo.Element("memes")).ToList(),
                PreceptDefNames(ideo.Element("precepts")).ToList(),
                PreceptSummaries(ideo.Element("precepts")).ToList(),
                StyleCategoryDefNames(ideo.Element("thingStyleCategories")).ToList(),
                Bool(ideo, "hidden"),
                Bool(ideo, "initialPlayerIdeo"),
                ideo.Element("memes")?.Elements("li").Count() ?? 0,
                ideo.Element("precepts")?.Elements("li").Count() ?? 0);
        }
    }

    private static string BuildIdeoKey(SnapshotIdentity identity, string localIdeoId)
    {
        return $"{Segment("owner", identity.OwnerId)}/{Segment("colony", identity.ColonyId)}/{Segment("snapshot", identity.SnapshotId)}/ideo:{localIdeoId}";
    }

    private static string Segment(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? $"{name}:unknown" : $"{name}:{value}";
    }

    private static IEnumerable<string> DefNames(XElement? list)
    {
        if (list is null)
        {
            return Enumerable.Empty<string>();
        }

        return list.Elements("li")
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IEnumerable<string> PreceptDefNames(XElement? list)
    {
        if (list is null)
        {
            return Enumerable.Empty<string>();
        }

        return list.Elements("li")
            .Select(item => Text(item, "def"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    private static IEnumerable<IdeoPreceptSummary> PreceptSummaries(XElement? list)
    {
        if (list is null)
        {
            return Enumerable.Empty<IdeoPreceptSummary>();
        }

        return list.Elements("li")
            .Select(item => new IdeoPreceptSummary(
                Text(item, "def"),
                item.Attribute("Class")?.Value,
                Text(item, "apparelDef"),
                Text(item, "noble"),
                Text(item, "despised"),
                Text(item, "targetGender"),
                Text(item, "overrideGender")))
            .Where(summary => !string.IsNullOrWhiteSpace(summary.DefName));
    }

    private static IEnumerable<string> StyleCategoryDefNames(XElement? list)
    {
        if (list is null)
        {
            return Enumerable.Empty<string>();
        }

        return list.Elements("li")
            .Select(item => Text(item, "category"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    private static string? Text(XElement? element, string name)
    {
        return element?.Element(name)?.Value.Trim();
    }

    private static bool Bool(XElement? element, string name)
    {
        return bool.TryParse(Text(element, name), out bool value) && value;
    }
}
