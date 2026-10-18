using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientWorldMarkersPresentation : IClientWorldMarkersPresentation
{
    private readonly Action<string> status;
    internal ClientWorldMarkersPresentation(Action<string> status) => this.status = status;
    public void Status(string text) => status(text);
    public void Diagnose(string text, bool warning) { if (warning) Log.Warning(text); else ClashLog.Message(text); }
    public string Summary(IReadOnlyCollection<ModWorldMapMarkerDto>? markers)
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

}
