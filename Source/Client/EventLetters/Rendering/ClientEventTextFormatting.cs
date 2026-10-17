using System;

namespace AIRsLight.ClashOfRim.EventLetters;

internal static class ClientEventTextFormatting
{
    internal static string FormatEventGroupName(string group)
    {
        return group switch
        {
            "Unknown" => ClashOfRimText.Key("ClashOfRim.EventGroup.Unknown"),
            "DirectlyProcessable" => ClashOfRimText.Key("ClashOfRim.EventGroup.DirectlyProcessable"),
            "WaitingForUserChoice" => ClashOfRimText.Key("ClashOfRim.EventGroup.WaitingForUserChoice"),
            "DeliveredUnconfirmed" => ClashOfRimText.Key("ClashOfRim.EventGroup.DeliveredUnconfirmed"),
            "Conflicts" => ClashOfRimText.Key("ClashOfRim.EventGroup.Conflicts"),
            "Rejected" => ClashOfRimText.Key("ClashOfRim.EventGroup.Rejected"),
            _ => group
        };
    }

    internal static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        string compact = value!.Replace("\r", string.Empty).Replace("\n", string.Empty);
        return compact.Length <= maxLength ? compact : compact.Substring(0, maxLength) + "...";
    }

}
