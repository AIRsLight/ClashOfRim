using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
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

    internal void ApplyCachedWorldMapMarkersToWorldObjects() => worldMarkers.ApplyCached();

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
