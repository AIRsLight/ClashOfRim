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
        => _ = initialWorldSetup.StartAsync(colonyAppearance.Capture());

    internal void StartUpdateServerWorldBaseline()
        => _ = worldWrites.StartAsync(ClientWorldWriteKind.Baseline, colonyAppearance.Capture());

    internal string BuildCurrentStorytellerDifficultySignature() => storytellerConfiguration.Capture().Signature;

    internal void SubmitRuntimeStorytellerSettingsIfChanged(string? initialSignature)
        => _ = worldWrites.StartAsync(ClientWorldWriteKind.Storyteller, colonyAppearance.Capture(), initialSignature);

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

}
