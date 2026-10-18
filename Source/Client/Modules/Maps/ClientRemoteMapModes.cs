using System;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal static class ClientRemoteMapModes
{
    internal const string Scout = "Scout";
    internal const string FriendlyObservation = "FriendlyObservation";
    internal const string RaidObservation = "RaidObservation";
    internal const string RaidBattle = "RaidBattle";
    internal const string RaidTarget = "RaidTarget";
    internal const string RaidPreparation = "RaidPreparation";

    internal static RemoteMapSessionKind Kind(string? mode)
    {
        if (string.Equals(mode, RaidBattle, StringComparison.OrdinalIgnoreCase)) return RemoteMapSessionKind.RaidBattle;
        if (string.Equals(mode, RaidObservation, StringComparison.OrdinalIgnoreCase)) return RemoteMapSessionKind.ObserveRaid;
        if (string.Equals(mode, FriendlyObservation, StringComparison.OrdinalIgnoreCase)) return RemoteMapSessionKind.ObserveAlly;
        return string.Equals(mode, Scout, StringComparison.OrdinalIgnoreCase) ? RemoteMapSessionKind.ScoutEnemy : RemoteMapSessionKind.None;
    }

    internal static string Normalize(string? mode) => Kind(mode) switch
    {
        RemoteMapSessionKind.RaidBattle => RaidBattle,
        RemoteMapSessionKind.ObserveRaid => RaidObservation,
        RemoteMapSessionKind.ObserveAlly => FriendlyObservation,
        _ => Scout
    };

    internal static string DownloadingKey(string mode) => Kind(mode) switch
    {
        RemoteMapSessionKind.ObserveRaid => "ClashOfRim.Observation.StatusDownloadingRaid",
        RemoteMapSessionKind.ObserveAlly => "ClashOfRim.Observation.StatusDownloadingFriendly",
        _ => "ClashOfRim.Observation.StatusDownloadingScout"
    };

    internal static string LoadedKey(string mode) => Kind(mode) switch
    {
        RemoteMapSessionKind.ObserveRaid => "ClashOfRim.Observation.RaidLoadedMessage",
        RemoteMapSessionKind.ObserveAlly => "ClashOfRim.Observation.FriendlyLoadedMessage",
        _ => "ClashOfRim.Observation.ScoutLoadedMessage"
    };
}
