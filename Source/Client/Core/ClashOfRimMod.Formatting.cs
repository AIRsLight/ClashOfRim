using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private static string FormatPlayers(IReadOnlyCollection<ModPlayerSummaryDto>? players)
    {
        if (players is null || players.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Players.NoneRegistered");
        }

        return ClashOfRimText.Key("ClashOfRim.Players.SummaryPrefix") + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), players
            .OrderByDescending(player => player.Online)
            .ThenBy(player => player.UserId, StringComparer.Ordinal)
            .Take(8)
            .Select(player =>
            {
                string online = player.Online
                    ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online")
                    : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline");
                string playerName = string.IsNullOrWhiteSpace(player.DisplayName)
                    ? player.UserId
                    : player.DisplayName!;
                return ClashOfRimText.Key(
                    "ClashOfRim.Players.SummaryItem",
                    ClashOfRimText.SafeArgument(playerName).Named("PLAYER"),
                    online.Named("STATUS"));
            }));
    }

    private bool HasTarget(out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(settings.TargetUserId)
            || string.IsNullOrWhiteSpace(settings.TargetColonyId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Target.StatusMissing");
            return false;
        }

        if (string.Equals(settings.TargetUserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(settings.TargetColonyId, settings.ColonyId, StringComparison.Ordinal))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Target.StatusSelf");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static string FormatCreationResult(
        ClashOfRimClientNetworkResult<ModEventCreationResponseDto> created,
        string label)
    {
        if (!created.Success || created.Response is null)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.CreationResult.Failed",
                label.Named("LABEL"),
                (created.ErrorCode ?? string.Empty).Named("CODE"),
                (created.Message ?? string.Empty).Named("MESSAGE"));
        }

        if (created.Response.Result is not null && !created.Response.Result.Accepted)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.CreationResult.Rejected",
                label.Named("LABEL"),
                created.Response.Result.ErrorCode.Named("CODE"),
                (created.Response.Result.Message ?? string.Empty).Named("MESSAGE"));
        }

        return ClashOfRimText.Key(
            "ClashOfRim.CreationResult.Created",
            label.Named("LABEL"));
    }

    private void CaptureWorldMapMarkers(
        ModWorldMapMarkerDeliveryDto? delivery,
        bool applyImmediately = false,
        string? source = null)
    {
        List<ModWorldMapMarkerDto> markers = delivery?.Markers?.ToList() ?? new List<ModWorldMapMarkerDto>();
        lock (eventStateLock)
        {
            lastWorldMapMarkers.Clear();
            lastWorldMapMarkers.AddRange(markers);
            giftsEnabled = delivery?.GiftsEnabled ?? true;
            pvpEnabled = delivery?.PvpEnabled ?? true;
        }

        MergeOccupiedPlayerColonySitesFromWorldMapMarkers(markers);
        string sourceLabel = string.IsNullOrWhiteSpace(source)
            ? "unspecified"
            : source!;
        ClashLog.Message("[ClashOfRim] Captured world map markers from "
            + sourceLabel
            + ": "
            + DescribeWorldMapMarkerDelivery(delivery)
            + ", "
            + DescribeWorldMapMarkerSample(markers));
        if (applyImmediately)
        {
            worldMapStatus = FormatWorldMapMarkers(markers);
            RemoteWorldObjectApplyResult applyResult =
                RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
                ClashLog.Message("[ClashOfRim] Applied world map markers immediately from " + sourceLabel + ": " + applyResult);
        }
        else if (Find.WorldObjects is not null)
        {
            EnqueueClashOfRimMainThreadAction(() =>
            {
                worldMapStatus = FormatWorldMapMarkers(markers);
                RemoteWorldObjectApplyResult applyResult =
                    RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
                ClashLog.Message("[ClashOfRim] Applied world map markers on main thread from " + sourceLabel + ": " + applyResult);
            });
        }
        else
        {
            EnqueueClashOfRimMainThreadAction(() =>
            {
                worldMapStatus = FormatWorldMapMarkers(markers);
            });
            Log.Warning("[ClashOfRim] Captured world map markers but Find.WorldObjects is null; cached markers will be applied when the world page is ready.");
        }
    }

    internal void ApplyCachedWorldMapMarkersToWorldObjects()
    {
        if (Find.WorldObjects is null || string.IsNullOrWhiteSpace(settings.UserId))
        {
            return;
        }

        List<ModWorldMapMarkerDto> markers;
        lock (eventStateLock)
        {
            if (lastWorldMapMarkers.Count == 0)
            {
                return;
            }

            markers = lastWorldMapMarkers.ToList();
        }

        RemoteWorldObjectApplyResult applyResult =
            RemoteRuntimeWorldObjectRegistry.Apply(markers, settings.UserId);
        if (applyResult.Created > 0 || applyResult.Removed > 0 || applyResult.Failed > 0)
        {
            ClashLog.Message("[ClashOfRim] Applied cached world map markers on starting-site GUI: " + applyResult);
        }
    }

    private static string FormatWorldMapMarkers(IReadOnlyCollection<ModWorldMapMarkerDto>? markers)
    {
        if (markers is null || markers.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.WorldMap.StatusNoMarkers");
        }

        string summary = string.Join("；", markers
            .OrderBy(marker => marker.Tile)
            .ThenBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
            .Take(6)
            .Select(marker =>
            {
                string label = string.IsNullOrWhiteSpace(marker.Label)
                    ? marker.OwnerUserId
                    : marker.Label!;
                var actions = new List<string>();
                if (marker.CanTrade)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionTrade"));
                }

                if (marker.CanRaid)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionRaid"));
                }

                if (marker.CanReinforce)
                {
                    actions.Add(ClashOfRimText.Key("ClashOfRim.WorldMap.ActionReinforce"));
                }

                string actionText = actions.Count == 0 ? ClashOfRimText.Key("ClashOfRim.WorldMap.ActionNone") : string.Join("/", actions);
                return $"{label}@{marker.Tile} {actionText}";
            }));

        return ClashOfRimText.Key(
            "ClashOfRim.WorldMap.StatusMarkerSummary",
            markers.Count.Named("COUNT"),
            summary.Named("SUMMARY"));
    }

    private static long? ExtractNotificationVersion(string data)
    {
        return ExtractJsonLong(data, "\"NotificationVersion\":");
    }

    private static long? ExtractWorldConfigurationVersion(string data)
    {
        return ExtractJsonLong(data, "\"WorldConfigurationVersion\":");
    }

    private static long? ExtractJsonLong(string data, string marker)
    {
        int start = data.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        while (start < data.Length && char.IsWhiteSpace(data[start]))
        {
            start++;
        }

        int end = start;
        while (end < data.Length && char.IsDigit(data[end]))
        {
            end++;
        }

        return end > start && long.TryParse(data.Substring(start, end - start), out long value)
            ? value
            : null;
    }

}
