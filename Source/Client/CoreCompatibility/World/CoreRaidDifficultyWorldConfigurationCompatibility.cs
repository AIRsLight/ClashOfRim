using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal static class CoreRaidDifficultyWorldConfigurationCompatibility
{
    private const string ProviderId = "ludeon.rimworld.core";
    private const string Kind = "raid-difficulty-baseline";

    private static readonly DataContractJsonSerializerSettings JsonSerializerSettings = new()
    {
        UseSimpleDictionaryFormat = true
    };

    public static BuiltInCoreCompatibilityPackageDescriptor Descriptor { get; } =
        new("clashofrim.core.raid-difficulty-baseline", Apply, order: 110);

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            "ludeon.rimworld.core",
            () => true,
            new[] { Kind });
        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionHandler(
            ProviderId,
            Kind,
            CollectRaidDifficultyExtension,
            applier: null);
    }

    private static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectRaidDifficultyExtension(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        ModCoreRaidDifficultyBaselineDto? baseline = ReadCurrentBaseline();
        if (baseline is null)
        {
            return Array.Empty<ModWorldConfigurationExtensionDto>();
        }

        return new[]
        {
            new ModWorldConfigurationExtensionDto
            {
                ProviderId = ProviderId,
                Kind = Kind,
                SchemaVersion = "1",
                PayloadJson = Serialize(new[] { baseline }),
                Metadata = new Dictionary<string, string?>
                {
                    ["storyteller"] = baseline.StorytellerDefName,
                    ["difficulty"] = baseline.DifficultyDefName,
                    ["threatScale"] = baseline.ThreatScale,
                    ["curveCount"] = baseline.Curves.Count.ToString(CultureInfo.InvariantCulture)
                }
            }
        };
    }

    private static ModCoreRaidDifficultyBaselineDto? ReadCurrentBaseline()
    {
        Storyteller? storyteller = Find.Storyteller;
        StorytellerDef? storytellerDef = storyteller?.def;
        Difficulty? difficulty = storyteller?.difficulty;
        DifficultyDef? difficultyDef = storyteller?.difficultyDef;
        if (storytellerDef is null && difficulty is null && difficultyDef is null)
        {
            return null;
        }

        return new ModCoreRaidDifficultyBaselineDto
        {
            StorytellerDefName = storytellerDef?.defName,
            DifficultyDefName = difficultyDef?.defName,
            ThreatScale = FormatFloat(difficulty?.threatScale ?? difficultyDef?.threatScale),
            RaidLootPointsFactor = FormatFloat(difficulty?.raidLootPointsFactor ?? difficultyDef?.raidLootPointsFactor),
            MinThreatPointsRangeCeiling = FormatFloat(difficulty?.minThreatPointsRangeCeiling ?? difficultyDef?.minThreatPointsRangeCeiling),
            Curves = CollectRaidCurves(storytellerDef)
        };
    }

    private static List<ModCoreRaidDifficultyCurveDto> CollectRaidCurves(StorytellerDef? storytellerDef)
    {
        var curves = new List<ModCoreRaidDifficultyCurveDto>();
        if (storytellerDef is null)
        {
            return curves;
        }

        AppendCurves(curves, "StorytellerDef", storytellerDef.defName, storytellerDef);
        foreach (StorytellerCompProperties comp in storytellerDef.comps ?? Enumerable.Empty<StorytellerCompProperties>())
        {
            AppendCurves(curves, "StorytellerComp", comp.GetType().FullName ?? comp.GetType().Name, comp);
        }

        return curves
            .Where(curve => curve.Points.Count > 0)
            .GroupBy(curve => curve.OwnerKind + "\u001f" + curve.OwnerName + "\u001f" + curve.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(curve => curve.OwnerKind, StringComparer.Ordinal)
            .ThenBy(curve => curve.OwnerName, StringComparer.Ordinal)
            .ThenBy(curve => curve.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void AppendCurves(
        List<ModCoreRaidDifficultyCurveDto> target,
        string ownerKind,
        string ownerName,
        object source)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (FieldInfo field in source.GetType().GetFields(flags))
        {
            if (!typeof(SimpleCurve).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            SimpleCurve? curve = field.GetValue(source) as SimpleCurve;
            ModCoreRaidDifficultyCurveDto? dto = BuildCurve(ownerKind, ownerName, field.Name, curve);
            if (dto is not null)
            {
                target.Add(dto);
            }
        }

        foreach (PropertyInfo property in source.GetType().GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || !typeof(SimpleCurve).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            SimpleCurve? curve;
            try
            {
                curve = property.GetValue(source) as SimpleCurve;
            }
            catch (Exception)
            {
                continue;
            }

            ModCoreRaidDifficultyCurveDto? dto = BuildCurve(ownerKind, ownerName, property.Name, curve);
            if (dto is not null)
            {
                target.Add(dto);
            }
        }
    }

    private static ModCoreRaidDifficultyCurveDto? BuildCurve(
        string ownerKind,
        string ownerName,
        string name,
        SimpleCurve? curve)
    {
        if (curve is null || curve.PointsCount <= 0)
        {
            return null;
        }

        return new ModCoreRaidDifficultyCurveDto
        {
            OwnerKind = ownerKind,
            OwnerName = ownerName,
            Name = name,
            Points = curve.Points
                .Select(point => new ModCoreRaidDifficultyCurvePointDto
                {
                    X = point.x,
                    Y = point.y
                })
                .ToList()
        };
    }

    private static string? FormatFloat(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Serialize<T>(IReadOnlyList<T> payload)
    {
        var serializer = new DataContractJsonSerializer(typeof(List<T>), JsonSerializerSettings);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, payload.ToList());
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

[DataContract]
internal sealed class ModCoreRaidDifficultyBaselineDto
{
    [DataMember(Name = "storytellerDefName")]
    public string? StorytellerDefName { get; set; }

    [DataMember(Name = "difficultyDefName")]
    public string? DifficultyDefName { get; set; }

    [DataMember(Name = "threatScale")]
    public string? ThreatScale { get; set; }

    [DataMember(Name = "raidLootPointsFactor")]
    public string? RaidLootPointsFactor { get; set; }

    [DataMember(Name = "minThreatPointsRangeCeiling")]
    public string? MinThreatPointsRangeCeiling { get; set; }

    [DataMember(Name = "curves")]
    public List<ModCoreRaidDifficultyCurveDto> Curves { get; set; } = new();
}

[DataContract]
internal sealed class ModCoreRaidDifficultyCurveDto
{
    [DataMember(Name = "ownerKind")]
    public string OwnerKind { get; set; } = string.Empty;

    [DataMember(Name = "ownerName")]
    public string OwnerName { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "points")]
    public List<ModCoreRaidDifficultyCurvePointDto> Points { get; set; } = new();
}

[DataContract]
internal sealed class ModCoreRaidDifficultyCurvePointDto
{
    [DataMember(Name = "x")]
    public float X { get; set; }

    [DataMember(Name = "y")]
    public float Y { get; set; }
}
