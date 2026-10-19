using System;
using System.Diagnostics.CodeAnalysis;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal static class ClientRaidTargets
{
    internal static bool IsRaidable([NotNullWhen(true)] ModWorldMapMarkerDto? target) => target?.CanRaid == true;

    internal static string? Rejection(ModWorldMapMarkerDto? target, bool enabled)
        => !enabled ? "ClashOfRim.Raid.PvpDisabled"
            : !IsRaidable(target) || string.IsNullOrWhiteSpace(target.OwnerUserId) || string.IsNullOrWhiteSpace(target.OwnerColonyId)
                || string.IsNullOrWhiteSpace(target.WorldObjectId) || string.IsNullOrWhiteSpace(target.MapId)
                ? "ClashOfRim.Raid.StatusTargetIncomplete" : null;
}
