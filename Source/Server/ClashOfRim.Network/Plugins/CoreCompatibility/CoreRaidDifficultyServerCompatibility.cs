using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;

internal static class CoreRaidDifficultyServerCompatibility
{
    internal const string ProviderId = "ludeon.rimworld.core";
    internal const string RaidDifficultyBaseline = "raid-difficulty-baseline";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private const float GlobalPointsMin = 35f;
    private const float GlobalPointsMax = 10000f;

    private static readonly IReadOnlyList<CurvePointData> PointsPerWealthCurve = new[]
    {
        new CurvePointData(0f, 0f),
        new CurvePointData(14000f, 0f),
        new CurvePointData(400000f, 2400f),
        new CurvePointData(700000f, 3600f),
        new CurvePointData(1000000f, 4200f)
    };

    private static readonly IReadOnlyList<CurvePointData> PointsPerColonistByWealthCurve = new[]
    {
        new CurvePointData(0f, 15f),
        new CurvePointData(10000f, 15f),
        new CurvePointData(400000f, 140f),
        new CurvePointData(1000000f, 200f)
    };

    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.core.raid-difficulty-baseline",
            Name: "Built-in core raid difficulty baseline",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[] { RaidDifficultyBaseline },
            WorldConfigurationExtensionProviders: new IWorldConfigurationExtensionProvider[]
            {
                new CoreRaidDifficultyWorldConfigurationExtensionProvider()
            });

    internal static CoreRaidDifficultyBaselineDto? ReadBaseline(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        WorldConfigurationExtensionDto? extension = extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, ProviderId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, RaidDifficultyBaseline, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
        {
            return null;
        }

        try
        {
            List<CoreRaidDifficultyBaselineDto>? parsed = JsonSerializer.Deserialize<List<CoreRaidDifficultyBaselineDto>>(
                extension.PayloadJson!,
                PayloadJsonOptions);
            return parsed?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static WorldConfigurationExtensionDto? BuildBaselineExtension(CoreRaidDifficultyBaselineDto? baseline)
    {
        if (baseline is null)
        {
            return null;
        }

        string payloadJson = JsonSerializer.Serialize(new[] { baseline }, PayloadJsonOptions);
        return new WorldConfigurationExtensionDto(
            ProviderId,
            RaidDifficultyBaseline,
            "1",
            payloadJson,
            new Dictionary<string, string?>
            {
                ["storyteller"] = baseline.StorytellerDefName,
                ["difficulty"] = baseline.DifficultyDefName,
                ["threatScale"] = baseline.ThreatScale,
                ["curveCount"] = baseline.Curves.Count.ToString(CultureInfo.InvariantCulture)
            });
    }

    internal static int EstimateDefaultThreatPointsForSnapshot(
        SaveSnapshotIndex index,
        int defenderWealth,
        int minimumDefenderWealth,
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        int wealth = Math.Max(0, Math.Max(defenderWealth, minimumDefenderWealth));
        int colonists = index.Maps.Sum(map => Math.Max(0, map.PlayerColonistCount));
        if (colonists <= 0 && index.Maps.Count == 0 && index.HistoryPlayerColonistCount is int historyColonists)
        {
            colonists = Math.Max(0, historyColonists);
        }

        float wealthPoints = EvaluateCurve(PointsPerWealthCurve, wealth);
        float colonistPoints = colonists * EvaluateCurve(PointsPerColonistByWealthCurve, wealth);
        float threatScale = ParseBaselineFloat(ReadBaseline(extensions)?.ThreatScale, 1f);
        float points = (wealthPoints + colonistPoints) * Math.Max(0f, threatScale);
        return (int)Math.Ceiling(Math.Clamp(points, GlobalPointsMin, GlobalPointsMax));
    }

    private static float EvaluateCurve(IReadOnlyList<CurvePointData> curve, float x)
    {
        if (curve.Count == 0)
        {
            return 0f;
        }

        if (x <= curve[0].X)
        {
            return curve[0].Y;
        }

        for (int i = 1; i < curve.Count; i++)
        {
            CurvePointData previous = curve[i - 1];
            CurvePointData current = curve[i];
            if (x > current.X)
            {
                continue;
            }

            float span = current.X - previous.X;
            if (span <= 0f)
            {
                return current.Y;
            }

            float t = (x - previous.X) / span;
            return previous.Y + (current.Y - previous.Y) * t;
        }

        return curve[^1].Y;
    }

    private static float ParseBaselineFloat(string? value, float fallback)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && !float.IsNaN(parsed)
            && !float.IsInfinity(parsed)
            ? parsed
            : fallback;
    }

    private readonly record struct CurvePointData(float X, float Y);
}

internal sealed record CoreRaidDifficultyBaselineDto(
    string? StorytellerDefName,
    string? DifficultyDefName,
    string? ThreatScale,
    string? RaidLootPointsFactor,
    string? MinThreatPointsRangeCeiling,
    IReadOnlyList<CoreRaidDifficultyCurveDto> Curves);

internal sealed record CoreRaidDifficultyCurveDto(
    string OwnerKind,
    string OwnerName,
    string Name,
    IReadOnlyList<CoreRaidDifficultyCurvePointDto> Points);

internal sealed record CoreRaidDifficultyCurvePointDto(float X, float Y);
