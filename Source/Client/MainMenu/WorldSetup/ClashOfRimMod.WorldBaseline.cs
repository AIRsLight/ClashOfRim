using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;
using static AIRsLight.ClashOfRim.ClientWorldConfigurationRuntime;
using static AIRsLight.ClashOfRim.ClientWorldColonySitesRuntime;
using static AIRsLight.ClashOfRim.ClientWorldNameRuntime;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool TryGenerateWorldFromServerConfiguration(Window createWorldPage)
        => worldGeneration.TryStart(new ClientWorldGenerationPage(createWorldPage));

    internal bool IsBlockedByServerColonySite(int tile, out ModPlayerColonySiteDto? blockingSite)
        => occupiedColonySites.IsBlocked(tile, 0, out blockingSite);

    internal bool IsBlockedByServerColonySite(PlanetTile tile, out ModPlayerColonySiteDto? blockingSite)
    {
        blockingSite = null;
        return tile.Valid && occupiedColonySites.IsBlocked(tile.tileId, ReadPlanetTileLayerId(tile), out blockingSite);
    }

    internal static string FormatBlockedColonySite(ModPlayerColonySiteDto site)
    {
        string owner = string.IsNullOrWhiteSpace(site.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownOtherPlayer")
            : site.UserId;
        string label = string.IsNullOrWhiteSpace(site.Label)
            ? site.ColonyId ?? string.Empty
            : site.Label ?? string.Empty;
        return string.IsNullOrWhiteSpace(label)
            ? $"{owner}@{site.Tile}"
            : $"{owner}/{label}@{site.Tile}";
    }

    internal void SubmitInitialWorldConfigurationIfPending()
        => _ = initialWorldSetup.StartAsync(BuildCurrentColonyAppearanceDto(settings));

    internal void StartUpdateServerWorldBaseline()
        => _ = worldWrites.StartAsync(ClientWorldWriteKind.Baseline, BuildCurrentColonyAppearanceDto(settings));

    internal string BuildCurrentStorytellerDifficultySignature() => storytellerConfiguration.Capture().Signature;

    internal void SubmitRuntimeStorytellerSettingsIfChanged(string? initialSignature)
        => _ = worldWrites.StartAsync(ClientWorldWriteKind.Storyteller, BuildCurrentColonyAppearanceDto(settings), initialSignature);

    private void ApplyServerWorldConfigurationExtensionCatalog(ModWorldConfigurationDto? configuration)
        => worldConfiguration.ApplyCatalog(configuration);

    private void ApplyRuntimeWorldConfigurationUpdate(ModWorldConfigurationDto configuration)
        => worldConfiguration.ApplyCatalog(configuration, applyStoryteller: true);

    internal bool TryApplyServerStorytellerBaselineAndContinue(Window storytellerPage)
        => storytellerConfiguration.ContinueSetup(new ClientStorytellerPage(storytellerPage));

    internal void LockMultiplayerCommitmentModeIfNeeded() => storytellerConfiguration.LockCommitment();

    internal static void DebugLogFlow(string stage, string? detail = null)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail;
        ClashLog.Message("[ClashOfRim.Flow] " + stage + suffix);
    }

    private static string DescribeCurrentWorldState()
    {
        try
        {
            object? world = Find.World;
            object? worldInfo = ReadMember(world, "info") ?? ReadMember(world, "Info");
            int factionCount = Find.World?.factionManager?.AllFactionsListForReading?.Count ?? -1;
            int objectCount = Find.WorldObjects?.AllWorldObjects?.Count ?? -1;
            int featureCount = Find.WorldFeatures?.features?.Count ?? -1;
            int tileCount = Find.WorldGrid?.TilesCount ?? -1;
            return $"seed={ReadFirstString(worldInfo, "seedString", "SeedString", "seed") ?? "<null>"}, coverage={ReadFirstString(worldInfo, "planetCoverage", "PlanetCoverage") ?? "<null>"}, rainfall={ReadFirstString(worldInfo, "overallRainfall", "OverallRainfall") ?? "<null>"}, temperature={ReadFirstString(worldInfo, "overallTemperature", "OverallTemperature") ?? "<null>"}, population={ReadFirstString(worldInfo, "overallPopulation", "OverallPopulation") ?? "<null>"}, landmarkDensity={ReadFirstString(worldInfo, "landmarkDensity", "LandmarkDensity") ?? "<null>"}, tileCount={tileCount}, factions={factionCount}, features={featureCount}, worldObjects={objectCount}";
        }
        catch (Exception ex)
        {
            return "failed:" + ex.GetType().Name + " " + ex.Message;
        }
    }

    private static string DescribeWorldMapMarkerDelivery(ModWorldMapMarkerDeliveryDto? delivery)
    {
        if (delivery is null)
        {
            return "delivery=<null>";
        }

        int tradeable = delivery.Markers.Count(marker => string.Equals(marker.Kind, "TradeableColony", StringComparison.Ordinal));
        int runtime = delivery.Markers.Count(marker => marker.Kind is "RuntimeCaravan" or "RuntimeShuttle" or "RuntimeTransportPod" or "RuntimeWorldObject");
        int raid = delivery.Markers.Count(marker => string.Equals(marker.Kind, "ActiveRaidTarget", StringComparison.Ordinal));
        return $"deliveryUser={delivery.UserId}, generatedAt={delivery.GeneratedAtUtc}, markers={delivery.Markers.Count}, tradeable={tradeable}, runtime={runtime}, raid={raid}";
    }

    private static string DescribeWorldMapMarkerSample(IReadOnlyList<ModWorldMapMarkerDto> markers)
    {
        if (markers.Count == 0)
        {
            return "sample=<empty>";
        }

        return "sample=" + string.Join(" | ", markers
            .OrderBy(marker => marker.Tile)
            .ThenBy(marker => marker.MarkerId, StringComparer.Ordinal)
            .Take(5)
            .Select(marker => $"id={marker.MarkerId}, kind={marker.Kind}, owner={marker.OwnerUserId}, colony={marker.OwnerColonyId}, tile={marker.Tile}, label={marker.Label}, trade={marker.CanTrade}, raid={marker.CanRaid}, reinforce={marker.CanReinforce}, appearance={DescribeAppearance(marker.Appearance)}"));
    }

    private static string DescribePlayerColonySiteSample(IReadOnlyList<ModPlayerColonySiteDto> sites)
    {
        if (sites.Count == 0)
        {
            return "<empty>";
        }

        return string.Join(" | ", sites
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.UserId, StringComparer.Ordinal)
            .Take(5)
            .Select(site => $"owner={site.UserId}, colony={site.ColonyId}, tile={site.Tile}, worldObject={site.WorldObjectId}, map={site.MapUniqueId}, label={site.Label}, appearance={DescribeAppearance(site.Appearance)}"));
    }

    private static string DescribeAppearance(ModColonyAppearanceDto? appearance)
    {
        if (appearance is null)
        {
            return "<none>";
        }

        return (appearance.Mode ?? string.Empty)
            + "/"
            + (appearance.IconDefName ?? string.Empty)
            + "/"
            + (appearance.ColorDefName ?? appearance.ColorHex ?? string.Empty);
    }

    internal static string DescribeWindow(Window? window)
    {
        if (window is null)
        {
            return "<null>";
        }

        string result = window.GetType().Name;
        if (window is Page page)
        {
            result += $"(prev={page.prev?.GetType().Name ?? "<null>"}, next={page.next?.GetType().Name ?? "<null>"}, nextAct={DescribeAction(page.nextAct)})";
        }

        result += $"[absorb={window.absorbInputAroundWindow}, preventCamera={window.preventCameraMotion}, layer={window.layer}]";
        return result;
    }

    internal static string DescribeWindowStack()
    {
        if (Find.WindowStack is null)
        {
            return "WindowStack=<null>";
        }

        List<string> windows = new();
        for (int i = 0; i < Find.WindowStack.Count; i++)
        {
            windows.Add(i + ":" + DescribeWindow(Find.WindowStack[i]));
        }

        return "WindowStack.Count=" + Find.WindowStack.Count + " [" + string.Join("; ", windows) + "]";
    }

    internal static string DescribeAction(Action? action)
    {
        return action is null
            ? "<null>"
            : action.Method.DeclaringType?.Name + "." + action.Method.Name;
    }

    private static List<ModRuntimeWorldObjectMarkerDto> ReadCurrentRuntimeWorldObjects()
    {
        var markers = new List<ModRuntimeWorldObjectMarkerDto>();
        if (Find.WorldObjects?.AllWorldObjects is null)
        {
            return markers;
        }

        foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
        {
            if (!IsRuntimeWorldObjectForSync(worldObject) || !IsValidTile(worldObject.Tile))
            {
                continue;
            }

            string? worldObjectId = ReadWorldObjectId(worldObject, worldObject.Tile);
            if (string.IsNullOrWhiteSpace(worldObjectId))
            {
                continue;
            }

            markers.Add(new ModRuntimeWorldObjectMarkerDto
            {
                WorldObjectId = worldObjectId!,
                DefName = worldObject.def?.defName,
                Kind = ResolveRuntimeWorldObjectKind(worldObject),
                Tile = worldObject.Tile,
                TileLayerId = ReadPlanetTileLayerId(worldObject.Tile),
                Label = worldObject.Label,
                PathTiles = ReadRuntimeWorldObjectPathTiles(worldObject)
            });
        }

        return markers;
    }

    private static List<int>? ReadRuntimeWorldObjectPathTiles(WorldObject worldObject)
    {
        if (worldObject is not Caravan caravan
            || caravan.pather is null
            || !caravan.pather.Moving
            || caravan.pather.curPath is null
            || !caravan.pather.curPath.Found
            || caravan.pather.curPath.NodesLeftCount < 2)
        {
            return null;
        }

        var pathTiles = new List<int>(caravan.pather.curPath.NodesLeftCount);
        for (int i = 0; i < caravan.pather.curPath.NodesLeftCount; i++)
        {
            PlanetTile tile = caravan.pather.curPath.Peek(i);
            if (IsValidTile(tile))
            {
                pathTiles.Add(tile);
            }
        }

        return pathTiles.Count >= 2 ? pathTiles : null;
    }

    private static bool IsRuntimeWorldObjectForSync(WorldObject worldObject)
    {
        if (worldObject is null
            || worldObject is RemoteRuntimeWorldObject
            || worldObject is RemoteColonyMapParent
            || worldObject is RemoteSessionMapParent
            || worldObject is Settlement
            || worldObject.def is null
            || worldObject.Faction != Faction.OfPlayer)
        {
            return false;
        }

        string className = worldObject.GetType().Name;
        string defName = worldObject.def.defName ?? string.Empty;
        return worldObject is Caravan
            || ContainsRuntimeWorldObjectToken(className)
            || ContainsRuntimeWorldObjectToken(defName);
    }

    private static string ResolveRuntimeWorldObjectKind(WorldObject worldObject)
    {
        string className = worldObject.GetType().Name;
        string defName = worldObject.def?.defName ?? string.Empty;
        if (worldObject is Caravan || ContainsToken(className, "Caravan") || ContainsToken(defName, "Caravan"))
        {
            return "Caravan";
        }

        if (ContainsToken(className, "Shuttle") || ContainsToken(defName, "Shuttle"))
        {
            return "Shuttle";
        }

        if (ContainsToken(className, "TransportPod") || ContainsToken(defName, "TransportPod"))
        {
            return "TransportPod";
        }

        return "WorldObject";
    }

    private static bool ContainsRuntimeWorldObjectToken(string value)
    {
        return ContainsToken(value, "Caravan")
            || ContainsToken(value, "Shuttle")
            || ContainsToken(value, "TransportPod")
            || ContainsToken(value, "Traveling");
    }

    private static bool ContainsToken(string value, string token)
    {
        return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildPlayerColonySiteSignature(
        IEnumerable<ModPlayerColonySiteDto> sites,
        IEnumerable<ModWorldConfigurationExtensionDto>? extensions = null)
    {
        string siteSignature = string.Join("|", sites
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.WorldObjectId, StringComparer.Ordinal)
            .ThenBy(site => site.MapUniqueId, StringComparer.Ordinal)
            .Select(site =>
                (site.UserId ?? string.Empty) + "\u001f"
                + (site.ColonyId ?? string.Empty) + "\u001f"
                + (site.WorldObjectId ?? string.Empty) + "\u001f"
                + (site.MapUniqueId ?? string.Empty) + "\u001f"
                + site.Tile.ToString(CultureInfo.InvariantCulture) + "\u001f"
                + site.TileLayerId.ToString(CultureInfo.InvariantCulture) + "\u001f"
                + (site.Label ?? string.Empty) + "\u001f"
                + (site.FactionName ?? string.Empty) + "\u001f"
                + BuildColonyAppearanceSignature(site.Appearance)));
        return siteSignature + "\u001e" + BuildWorldConfigurationExtensionSignature(
            extensions ?? Enumerable.Empty<ModWorldConfigurationExtensionDto>());
    }

    private static ModColonyAppearanceDto? BuildCurrentColonyAppearanceDto(ClashOfRimSettings settings)
    {
        ColonyAppearanceSelection appearance = ReadCurrentColonyAppearanceSelection(settings);
        if (!appearance.HasAny)
        {
            return null;
        }

        return new ModColonyAppearanceDto
        {
            Mode = NullIfWhiteSpace(appearance.Mode),
            IconDefName = NullIfWhiteSpace(appearance.IconDefName),
            ColorDefName = NullIfWhiteSpace(appearance.ColorDefName),
            ColorHex = NullIfWhiteSpace(appearance.ColorHex)
        };
    }

    private static string BuildColonyAppearanceSignature(ModColonyAppearanceDto? appearance)
    {
        if (appearance is null)
        {
            return string.Empty;
        }

        return (appearance.Mode ?? string.Empty) + "\u001d"
            + (appearance.IconDefName ?? string.Empty) + "\u001d"
            + (appearance.ColorDefName ?? string.Empty) + "\u001d"
            + (appearance.ColorHex ?? string.Empty);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string BuildWorldConfigurationExtensionSignature(IEnumerable<ModWorldConfigurationExtensionDto> extensions)
    {
        return string.Join("|", extensions
            .OrderBy(extension => extension.ProviderId, StringComparer.Ordinal)
            .ThenBy(extension => extension.Kind, StringComparer.Ordinal)
            .Select(extension =>
                (extension.ProviderId ?? string.Empty) + "\u001f"
                + (extension.Kind ?? string.Empty) + "\u001f"
                + (extension.SchemaVersion ?? string.Empty) + "\u001f"
                + (extension.PayloadJson ?? string.Empty)));
    }

    private static bool HasLoadedPlayerColonyContext()
    {
        if (Find.CurrentMap is not null)
        {
            return true;
        }

        return Find.Maps?.Any(IsPlayerColonyMap) == true;
    }

    private static string DescribePlayerColonyScanContext()
    {
        int maps = Find.Maps?.Count ?? -1;
        int worldObjects = Find.WorldObjects?.AllWorldObjects?.Count ?? -1;
        string currentMap = Find.CurrentMap is null
            ? "<null>"
            : "Map_" + Find.CurrentMap.uniqueID.ToString(CultureInfo.InvariantCulture);
        string playerWorldObjectSample = string.Join(
            "; ",
            (Find.WorldObjects?.AllWorldObjects ?? Enumerable.Empty<WorldObject>())
            .Where(IsPlayerColonyWorldObject)
            .Take(5)
            .Select(worldObject =>
                ReadFirstString(ReadMember(worldObject, "def"), "defName", "Name")
                + "@"
                + ReadFirstInt(worldObject, "Tile", "tile")?.ToString(CultureInfo.InvariantCulture)));

        return "context=programState:"
            + Current.ProgramState
            + ", maps:"
            + maps.ToString(CultureInfo.InvariantCulture)
            + ", currentMap:"
            + currentMap
            + ", worldObjects:"
            + worldObjects.ToString(CultureInfo.InvariantCulture)
            + ", playerWorldObjects:"
            + (string.IsNullOrWhiteSpace(playerWorldObjectSample) ? "<empty>" : playerWorldObjectSample);
    }

}
